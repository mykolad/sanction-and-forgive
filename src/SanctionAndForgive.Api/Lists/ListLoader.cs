using Microsoft.EntityFrameworkCore;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Lists;

/// <summary>Loads the lists from SQL into the <see cref="ListCatalog"/>. Only lists whose active import changed are
/// rebuilt, one at a time, so memory holds at most one list twice (section 3). If the database is unreachable the
/// catalog keeps serving what it has: each list then turns Not checked once it passes its allowed age.</summary>
public sealed class ListLoader(IServiceScopeFactory scopes, ListCatalog catalog, ILogger<ListLoader> logger)
{
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
        var sources = await context.Sources.AsNoTracking().OrderBy(source => source.Code)
            .Select(source => new { source.Code, source.Name, source.Url, source.MaxAgeHours, source.ActiveImportId, source.LastCheckedAt, RecordCount = source.ActiveImport != null ? source.ActiveImport!.RecordCount : 0 })
            .ToListAsync(cancellationToken);

        var current = catalog.Lists.ToDictionary(list => list.Code, StringComparer.Ordinal);
        var lists = new List<LoadedList>(sources.Count);
        foreach (var source in sources)
        {
            var existing = current.GetValueOrDefault(source.Code);
            var index = existing is not null && existing.ActiveImportId == source.ActiveImportId ? existing.Index : null;
            if (index is null && source.ActiveImportId is { } importId)
            {
                index = await BuildAsync(context, source.Code, importId, cancellationToken);
            }

            lists.Add(new LoadedList(source.Code, source.Name, source.Url, source.MaxAgeHours, source.ActiveImportId, source.LastCheckedAt, source.RecordCount, index));
        }

        // One swap per refresh, with every source in it: a list never silently disappears from the rows (it would
        // be missing instead of Not checked), and the first swap is what makes the API ready.
        catalog.Replace(lists);
    }

    private async Task<ListIndex?> BuildAsync(ListsDbContext context, string code, int importId, CancellationToken cancellationToken)
    {
        try
        {
            var subjects = await context.Subjects.AsNoTracking().Where(subject => subject.ImportId == importId)
                .Include(subject => subject.Names).Include(subject => subject.Addresses).AsSplitQuery()
                .ToListAsync(cancellationToken);
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
/// then checks every <c>Lists:RefreshMinutes</c> (15) for changed imports and fresher check times.</summary>
public sealed class ListRefreshService(ListLoader loader, IConfiguration configuration, ILogger<ListRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(configuration.GetValue("Lists:RefreshMinutes", 15));
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
