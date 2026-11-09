using System.Data;
using Microsoft.EntityFrameworkCore;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Lists;

/// <summary>Loads the lists from SQL into the <see cref="ListCatalog"/>. Only lists whose active import changed are
/// rebuilt, one at a time, and once the API is ready each is published as soon as it's built, so its old index can
/// go: memory holds at most one list twice (section 3). Each list is read in one snapshot transaction and checked
/// against its import's record count, so a concurrent cleanup can never make half a list searchable; a list that
/// fails either stays Not checked. If the database is unreachable the catalog keeps serving what it has, and lists
/// age out.</summary>
public sealed partial class ListLoader(IServiceScopeFactory scopes, ListCatalog catalog, TimeProvider clock, ILogger<ListLoader> logger) : IDisposable
{
    /// <summary>How many of each source's latest imports the status page shows.</summary>
    /// <summary>How many updates <c>GET /api/v1/sources</c> shows per list; the catalog holds every one of them
    /// (<c>GET /api/v1/sources/{code}/history</c>): a row is an update, never an unchanged check, so they're few.</summary>
    public const int HistoryShown = 10;

    /// <summary>One refresh at a time (the timer's, or one an administrator's review starts): two would build the same
    /// list twice, and memory holds at most one list twice.</summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    public void Dispose() => gate.Dispose();

    /// <summary>Reloads the allow-list only (after an administrator's edit): a small read, no list is rebuilt. Under the
    /// same gate as a refresh, so an older read can never publish over a newer one. With <paramref name="withoutId"/>
    /// (a removal), that entry stops applying first, so a reload that fails leaves the list stricter, never looser.
    /// False if the reload failed (the next refresh retries).</summary>
    public async Task<bool> ReloadAllowListAsync(int? withoutId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (withoutId is { } id)
            {
                catalog.ReplaceAllowList(catalog.AllowList.Without(id));
            }

            return await TryLoadAllowListAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs an allow-list addition under the refresh gate, then reloads the allow-list if it was stored: the
    /// lists the name was checked against can't change between the check and the insert, and the entry is in use before
    /// any later refresh. <paramref name="add"/> returns its result and whether it stored an entry. The reload ignores
    /// cancellation (the entry is committed); its second value is false if the reload failed.</summary>
    public async Task<(T Result, bool Reloaded)> AddToAllowListAsync<T>(Func<Task<(T Result, bool Stored)>> add, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (result, stored) = await add();
            return (result, stored && await TryLoadAllowListAsync(CancellationToken.None));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> TryLoadAllowListAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await LoadAllowListAsync(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AllowListReloadFailed(logger, exception);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reloading the allow-list after an edit failed; the next refresh retries")]
    private static partial void AllowListReloadFailed(ILogger logger, Exception exception);

    private async Task LoadAllowListAsync(ListsDbContext context, CancellationToken cancellationToken)
    {
        var snapshot = await new AllowListStore(context, clock).CurrentAsync(cancellationToken);
        catalog.ReplaceAllowList(AllowList.Build(snapshot.Entries.Select(entry => new AllowListEntry(entry.Id, entry.Name, entry.Reason)), snapshot.Version));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshOnceAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
        var sources = await context.Sources.AsNoTracking().OrderBy(source => source.Code)
            .Select(source => new { source.Code, source.Name, source.Url, source.MaxAgeHours, source.ActiveImportId, source.LastCheckedAt, RecordCount = source.ActiveImport != null ? source.ActiveImport!.RecordCount : 0, UpdatedAt = source.ActiveImport != null ? source.ActiveImport!.CompletedAt : null, source.SwitchedOffAt, source.SwitchedOffReason })
            .ToListAsync(cancellationToken);

        await LoadAllowListAsync(context, cancellationToken);

        // The history first: a small read, and the status page shows a held update even while lists rebuild.
        var store = new ImportStore(context, clock);
        var history = await store.HistoryAsync(code: null, perSource: int.MaxValue, cancellationToken);
        catalog.ReplaceHeld((await store.HeldAsync(cancellationToken)).GroupBy(entry => entry.SourceCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));
        catalog.ReplaceHistory(history.GroupBy(entry => entry.SourceCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ImportHistoryEntry>)[.. group], StringComparer.Ordinal));

        var current = catalog.Lists.ToDictionary(list => list.Code, StringComparer.Ordinal);
        var lists = new LoadedList[sources.Count];
        var pending = new List<(int Position, LoadedList Target)>();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var switchedOff = source.SwitchedOffAt is { } at ? new ListSwitchedOff(at, source.SwitchedOffReason ?? string.Empty) : null;
            var target = new LoadedList(source.Code, source.Name, source.Url, source.MaxAgeHours, source.ActiveImportId, source.LastCheckedAt, source.UpdatedAt, source.RecordCount, Index: null)
            {
                SwitchedOff = switchedOff,
            };
            var existing = current.GetValueOrDefault(source.Code);
            if (existing is not null && existing.ActiveImportId == source.ActiveImportId && (existing.Index is not null || source.ActiveImportId is null))
            {
                // Unchanged: same index, fresh check time. (A list whose build failed is built again.)
                lists[i] = target with { Index = existing.Index };
                continue;
            }

            // Changed or new: until its new index is built, a changed list serves its previous one (with its previous
            // version and check time), and a new list is Not checked. A list never disappears from the rows. Switching
            // off or on applies at once either way.
            lists[i] = existing is null ? target : existing with { SwitchedOff = switchedOff };
            if (source.ActiveImportId is not null)
            {
                pending.Add((i, target));
            }
        }

        foreach (var (position, target) in pending)
        {
            // A build that fails or finds the list moved on keeps whatever served before (or Not checked for a new
            // list); the next refresh tries again.
            var index = await BuildAsync(context, target.Code, target.ActiveImportId!.Value, target.RecordCount, cancellationToken);
            lists[position] = index is not null ? target with { Index = index } : lists[position];
            if (catalog.IsReady)
            {
                catalog.Replace([.. lists]);
            }
        }

        // One full swap with every source in it: the first one is what makes the API ready.
        catalog.Replace(lists);
    }

    internal async Task<ListIndex?> BuildAsync(ListsDbContext context, string code, int importId, int expectedCount, CancellationToken cancellationToken)
    {
        try
        {
            var strategy = context.Database.CreateExecutionStrategy();
            var subjects = await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
                // Cleanup only ever deletes imports that are neither active nor the one before: if the source still
                // points at this import in this snapshot, nothing of it can have been deleted yet.
                var stillThere = await context.Sources.AnyAsync(source => source.Code == code && source.ActiveImportId == importId, cancellationToken);
                var loaded = stillThere
                    ? await context.Subjects.AsNoTracking().Where(subject => subject.ImportId == importId)
                        .Include(subject => subject.Names).Include(subject => subject.Addresses).AsSplitQuery()
                        .ToListAsync(cancellationToken)
                    : null;
                await transaction.CommitAsync(cancellationToken);
                return loaded;
            });

            if (subjects is null || subjects.Count != expectedCount)
            {
                logger.LogWarning("The {Code} import {ImportId} changed while loading ({Loaded} of {Expected} subjects); it stays Not checked until the next refresh", code, importId, subjects?.Count, expectedCount);
                return null;
            }

            var index = ListIndex.Build(subjects.Select(ToListed));
            logger.LogInformation("Built the {Code} index from import {ImportId}: {Subjects} subjects, {Aliases} aliases", code, importId, subjects.Count, index.AliasCount);
            return index;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Lists only, no input data: the list stays Not checked until a later refresh succeeds.
            logger.LogError(exception, "Building the {Code} index from import {ImportId} failed", code, importId);
            return null;
        }
    }

    private static ListedSubject ToListed(Subject subject) => new(
        subject.ExternalId,
        subject.Type switch
        {
            SubjectType.Individual => SubjectKind.Individual,
            SubjectType.Entity => SubjectKind.Entity,
            _ => SubjectKind.Unknown,
        },
        subject.PrimaryName,
        [.. subject.Names.Select(name => new ListedName(name.FullName, name.IsWeak))],
        [.. subject.Addresses.Select(address => new ListedAddress(address.Raw, address.CountryCode?.Trim()))],
        subject.Programmes,
        subject.SourceUrl);
}

/// <summary>Loads the lists at startup (in the background: the API listens at once and answers 503 until then),
/// then checks every <c>Lists:RefreshMinutes</c> (15) for changed imports and fresher check times. A fraction is
/// allowed: the conformance run uses 0.05 (3 seconds) so it sees each change it makes without waiting.</summary>
public sealed class ListRefreshService(ListLoader loader, IConfiguration configuration, ILogger<ListRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(configuration.GetValue("Lists:RefreshMinutes", 15.0));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await loader.RefreshAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Refreshing the lists failed; serving what was loaded before");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
