using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace SanctionAndForgive.Data;

public enum ImportResult
{
    /// <summary>A new import became active.</summary>
    Imported,

    /// <summary>The content hash matched the active import: only <see cref="Source.LastCheckedAt"/> moved.</summary>
    Unchanged,

    /// <summary>Zero records: the old list is kept, nothing to review.</summary>
    Rejected,

    /// <summary>A drop of more than 20%: written but held, not active, until it's confirmed or rejected. The old list
    /// keeps serving (and ages out as usual).</summary>
    HeldForReview,
}

/// <param name="ImportId">The import written or held (null when nothing was written).</param>
public sealed record ImportOutcome(ImportResult Result, int RecordCount, int? PreviousCount, string Message, int? ImportId);

/// <summary>What a review did: <c>Confirmed</c> (now active), <c>Rejected</c>, or why it couldn't
/// (<c>NotFound</c>, <c>NotPending</c>).</summary>
public enum ReviewResult
{
    Confirmed,
    Rejected,
    NotFound,
    NotPending,
}

/// <summary>One import of a source as the history shows it.</summary>
public sealed record ImportHistoryEntry(int Id, string SourceCode, ImportStatus Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int RecordCount, string? Message, bool Active, DateTimeOffset? ReviewedAt, string? ReviewedBy);

/// <summary>Writes imports (section 4, "Import rules"). Each import is a complete copy of a source's subjects, written
/// with <see cref="SqlBulkCopy"/> in batches, then made active in one transaction: readers never see half a list.
/// Every import's row stays as the history; the records are kept for the active import, the one before it, and one
/// held for review. One import per source runs at a time (an application lock).</summary>
public sealed class ImportStore(ListsDbContext context, TimeProvider clock)
{
    private const int BatchSize = 5000;

    /// <summary>External ids are keys (dedup, upsert, remove): longer ones are refused, never truncated.</summary>
    private const int MaxExternalId = 200;

    /// <summary>A drop of more than this fraction of the active import's records is refused without --force.</summary>
    private const double MaxDrop = 0.20;

    public async Task<Source> EnsureSourceAsync(string code, string name, string? url, int? maxAgeHours, CancellationToken cancellationToken)
    {
        var source = await context.Sources.SingleOrDefaultAsync(s => s.Code == code, cancellationToken);
        if (source is null)
        {
            source = new Source { Code = code, Name = name, Url = url, MaxAgeHours = maxAgeHours };
            context.Sources.Add(source);
        }
        else
        {
            source.Name = name;
            source.Url = url;
            source.MaxAgeHours = maxAgeHours;
        }

        await context.SaveChangesAsync(cancellationToken);
        return source;
    }

    public Task<Source?> FindSourceAsync(string code, CancellationToken cancellationToken) =>
        context.Sources.SingleOrDefaultAsync(s => s.Code == code, cancellationToken);

    /// <summary>Imports <paramref name="subjects"/> as <paramref name="source"/>'s new list, unless it's unchanged, has
    /// zero records (always refused), or dropped by more than 20% (held for review; <paramref name="force"/> imports it
    /// anyway, and imports even an unchanged list). Throws <see cref="ArgumentException"/> for an external id over 200 characters: ids are
    /// keys, never truncated.</summary>
    public Task<ImportOutcome> ImportAsync(Source source, IReadOnlyList<Subject> subjects, bool force, CancellationToken cancellationToken) =>
        LockedImportAsync(source, subjects, change: null, force ? ImportMode.Forced : ImportMode.Feed, cancellationToken);

    /// <summary>A manual change: <paramref name="change"/> gets the active list as it is under the source's lock, and
    /// its result becomes the new list. Reading, changing and activating all happen under one lock, so two edits
    /// can't overwrite each other. A change that changes nothing returns <c>Unchanged</c> and writes nothing; the drop
    /// guard doesn't apply (a manual list is what its owner says it is), the zero-record guard does.</summary>
    public Task<ImportOutcome> ChangeAsync(Source source, Func<List<Subject>, IReadOnlyList<Subject>> change, CancellationToken cancellationToken) =>
        LockedImportAsync(source, subjects: null, change, ImportMode.Manual, cancellationToken);

    /// <summary>Records a refresh that failed before it could import (download, parse): a <c>Failed</c> import with
    /// the reason, for <c>status</c>. The active list and <see cref="Source.LastCheckedAt"/> don't change.</summary>
    public async Task RecordFailureAsync(Source source, string message, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        context.Imports.Add(new Import { SourceId = source.Id, StartedAt = now, CompletedAt = now, Status = ImportStatus.Failed, Message = Truncate(message, 1000) });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Makes a held import active (<paramref name="confirm"/>) or rejects it and deletes its records, as
    /// <paramref name="reviewer"/>. Under the source's lock; the flip is conditional like any import's, so a list
    /// that changed meanwhile is never overwritten. A confirmed list counts as checked when it was downloaded.</summary>
    public async Task<ReviewResult> ReviewAsync(int importId, bool confirm, string reviewer, CancellationToken cancellationToken)
    {
        var held = await context.Imports.Include(i => i.Source).SingleOrDefaultAsync(i => i.Id == importId, cancellationToken);
        if (held?.Source is not { } source)
        {
            return ReviewResult.NotFound;
        }

        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await AcquireLockAsync(source.Code, cancellationToken);
            await context.Entry(source).ReloadAsync(cancellationToken);
            await context.Entry(held).ReloadAsync(cancellationToken);
            if (held.Status != ImportStatus.PendingReview || !held.HasData)
            {
                return ReviewResult.NotPending;
            }

            var now = clock.GetUtcNow();
            if (!confirm)
            {
                await RejectHeldAsync(held, "rejected on review", now, reviewer, cancellationToken);
                return ReviewResult.Rejected;
            }

            // Held against another list than the one active now: it no longer says what it said. Never roll back.
            if (held.BasedOnImportId != source.ActiveImportId)
            {
                await RejectHeldAsync(held, "superseded: the list changed after it was held", now, reviewer: null, cancellationToken);
                return ReviewResult.NotPending;
            }

            await ActivateAsync(source, held, source.ActiveImportId, held.StartedAt, new ReviewStamp(Truncate(reviewer, 256)!, now, Truncate(held.Message + "; confirmed on review", 1000)!), cancellationToken);
            await DeleteOldImportsAsync(source, cancellationToken);
            return ReviewResult.Confirmed;
        }
        finally
        {
            await ReleaseLockAsync(source.Code);
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Who this connection is signed in as (<c>SUSER_SNAME()</c>): the reviewer the CLI records.</summary>
    public async Task<string> CurrentLoginAsync(CancellationToken cancellationToken) =>
        (await context.Database.SqlQueryRaw<string>("SELECT SUSER_SNAME() AS Value").ToListAsync(cancellationToken)).FirstOrDefault() ?? "unknown";

    /// <summary>Every import held for review, newest first, however many checks came after it.</summary>
    public async Task<List<ImportHistoryEntry>> HeldAsync(CancellationToken cancellationToken)
    {
        var held = await context.Imports.AsNoTracking().Where(i => i.Status == ImportStatus.PendingReview && i.HasData)
            .OrderByDescending(i => i.Id).Select(i => new { Import = i, i.Source!.Code }).ToListAsync(cancellationToken);
        return [.. held.Select(h => new ImportHistoryEntry(h.Import.Id, h.Code, h.Import.Status, h.Import.StartedAt, h.Import.CompletedAt, h.Import.RecordCount, h.Import.Message, Active: false, h.Import.ReviewedAt, h.Import.ReviewedBy))];
    }

    /// <summary>The latest imports of every source (or of <paramref name="code"/>), newest first,
    /// <paramref name="perSource"/> each.</summary>
    public async Task<List<ImportHistoryEntry>> HistoryAsync(string? code, int perSource, CancellationToken cancellationToken)
    {
        var sources = await context.Sources.AsNoTracking().Where(s => code == null || s.Code == code).OrderBy(s => s.Code)
            .Select(s => new { s.Id, s.Code, s.ActiveImportId }).ToListAsync(cancellationToken);
        var history = new List<ImportHistoryEntry>();
        foreach (var source in sources)
        {
            // One small query per source (a dozen sources): the index on (SourceId, Status) serves it.
            var imports = await context.Imports.AsNoTracking().Where(i => i.SourceId == source.Id).OrderByDescending(i => i.Id).Take(perSource).ToListAsync(cancellationToken);
            history.AddRange(imports.Select(i => new ImportHistoryEntry(i.Id, source.Code, i.Status, i.StartedAt, i.CompletedAt, i.RecordCount, i.Message, source.ActiveImportId == i.Id, i.ReviewedAt, i.ReviewedBy)));
        }

        return history;
    }

    /// <summary>Forgets every tracked change after a failed source, so the next source's save can't retry it.</summary>
    public void Reset() => context.ChangeTracker.Clear();

    private async Task<ImportOutcome> LockedImportAsync(Source source, IReadOnlyList<Subject>? subjects, Func<List<Subject>, IReadOnlyList<Subject>>? change, ImportMode mode, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await AcquireLockAsync(source.Code, cancellationToken);

            // Another process may have imported since the source was loaded: decide on what's there now.
            await context.Entry(source).ReloadAsync(cancellationToken);
            var records = Deduplicate(subjects ?? change!(await ActiveSubjectsAsync(source, cancellationToken)));
            if (records.FirstOrDefault(subject => subject.ExternalId.Length > MaxExternalId) is { } tooLong)
            {
                throw new ArgumentException($"An external id is longer than {MaxExternalId} characters: {tooLong.ExternalId[..40]}…", nameof(subjects));
            }

            var hash = CanonicalHash(records);
            var active = source.ActiveImportId is { } activeId ? await context.Imports.SingleAsync(i => i.Id == activeId, cancellationToken) : null;
            var now = clock.GetUtcNow();

            var held = await context.Imports.AsNoTracking().Where(i => i.SourceId == source.Id && i.Status == ImportStatus.PendingReview).OrderByDescending(i => i.Id).ToListAsync(cancellationToken);
            if (mode != ImportMode.Forced && active?.ContentHash == hash)
            {
                // A feed checked successfully: it's current again, and an update held for review is moot. A manual
                // edit that changes nothing writes nothing.
                if (mode == ImportMode.Feed)
                {
                    await InTransactionAsync(async () =>
                    {
                        await context.Sources.Where(s => s.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(s => s.LastCheckedAt, now), cancellationToken);
                        await SupersedeHeldAsync(source.Id, exceptId: null, "superseded: the feed returned to the active list", cancellationToken);
                    }, cancellationToken);
                    await context.Entry(source).ReloadAsync(cancellationToken);
                    await DeleteOldImportsAsync(source, cancellationToken);
                }

                return new ImportOutcome(ImportResult.Unchanged, active.RecordCount, active.RecordCount, "Unchanged since the active import", null);
            }

            if (records.Count == 0)
            {
                context.Imports.Add(new Import { SourceId = source.Id, StartedAt = now, CompletedAt = now, ContentHash = hash, RecordCount = 0, Status = ImportStatus.Rejected, Message = "Zero records" });
                await context.SaveChangesAsync(cancellationToken);
                return new ImportOutcome(ImportResult.Rejected, 0, active?.RecordCount, "Zero records; the active list is kept", null);
            }

            var drop = mode == ImportMode.Feed && active is not null && records.Count < active.RecordCount * (1 - MaxDrop)
                ? $"Records dropped from {active.RecordCount} to {records.Count}, more than {MaxDrop:P0}: held for review"
                : null;
            if (drop is not null && held.FirstOrDefault() is { } same && same.ContentHash == hash)
            {
                // The same update again: it's already waiting.
                return new ImportOutcome(ImportResult.HeldForReview, records.Count, active?.RecordCount, $"Still held for review (import {same.Id}); the active list is kept", same.Id);
            }

            var import = new Import { SourceId = source.Id, StartedAt = now, ContentHash = hash, RecordCount = records.Count, Status = ImportStatus.Running, HasData = true, BasedOnImportId = drop is null ? null : active!.Id };
            context.Imports.Add(import);
            await context.SaveChangesAsync(cancellationToken);
            var (previousActive, previousChecked) = (source.ActiveImportId, source.LastCheckedAt);
            try
            {
                await WriteSubjectsAsync(source, import, records, cancellationToken);
                if (drop is not null)
                {
                    // Held, and any older hold superseded, in one transaction.
                    var completed = clock.GetUtcNow();
                    await InTransactionAsync(async () =>
                    {
                        await context.Imports.Where(i => i.Id == import.Id).ExecuteUpdateAsync(
                            set => set.SetProperty(i => i.Status, ImportStatus.PendingReview).SetProperty(i => i.CompletedAt, completed).SetProperty(i => i.Message, drop), cancellationToken);
                        await SupersedeHeldAsync(source.Id, import.Id, $"superseded by import {import.Id}", cancellationToken);
                    }, cancellationToken);
                    await context.Entry(import).ReloadAsync(cancellationToken);
                }
                else
                {
                    await ActivateAsync(source, import, previousActive, now, review: null, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is SqlException or DbUpdateException or RetryLimitExceededException or InvalidOperationException or OperationCanceledException)
            {
                // The activation may have committed before the failure surfaced: if so, the import is active and whole.
                var persisted = await context.Sources.AsNoTracking().Where(s => s.Id == source.Id).Select(s => s.ActiveImportId).SingleAsync(CancellationToken.None);
                if (persisted != import.Id)
                {
                    source.ActiveImportId = previousActive;
                    source.LastCheckedAt = previousChecked;
                    context.Entry(source).State = EntityState.Unchanged;
                    await DeleteImportDataAsync(import.Id, CancellationToken.None);
                    import.HasData = false;
                    import.Status = ImportStatus.Failed;
                    import.CompletedAt = clock.GetUtcNow();
                    import.Message = exception.GetType().Name;
                    await context.SaveChangesAsync(CancellationToken.None);
                    throw;
                }

                await context.Entry(source).ReloadAsync(CancellationToken.None);
                await context.Entry(import).ReloadAsync(CancellationToken.None);
            }

            if (drop is not null)
            {
                return new ImportOutcome(ImportResult.HeldForReview, records.Count, active?.RecordCount, $"{drop} (import {import.Id}); the active list is kept", import.Id);
            }

            await DeleteOldImportsAsync(source, cancellationToken);
            return new ImportOutcome(ImportResult.Imported, records.Count, active?.RecordCount, "Imported", import.Id);
        }
        finally
        {
            await ReleaseLockAsync(source.Code);
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>The active import's subjects with their names and addresses, detached: what a manual change starts from.</summary>
    public async Task<List<Subject>> ActiveSubjectsAsync(Source source, CancellationToken cancellationToken)
    {
        if (source.ActiveImportId is not { } activeId)
        {
            return [];
        }

        var subjects = await context.Subjects.AsNoTracking().Where(s => s.ImportId == activeId).Include(s => s.Names).Include(s => s.Addresses)
            .AsSplitQuery().ToListAsync(cancellationToken);
        return [.. subjects.Select(Detach)];
    }

    /// <summary>Every source with its active import, for <c>status</c> and the API.</summary>
    public async Task<List<SourceStatus>> StatusAsync(CancellationToken cancellationToken)
    {
        var sources = await context.Sources.AsNoTracking().Include(s => s.ActiveImport).OrderBy(s => s.Code).ToListAsync(cancellationToken);
        var latest = await context.Imports.AsNoTracking()
            .GroupBy(i => i.SourceId)
            .Select(group => group.OrderByDescending(i => i.Id).First())
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return
        [
            .. sources.Select(source =>
            {
                var last = latest.FirstOrDefault(i => i.SourceId == source.Id);
                var age = source.LastCheckedAt is { } checkedAt ? now - checkedAt : (TimeSpan?)null;
                var current = source.ActiveImport is not null && (source.MaxAgeHours is null || (age is not null && age.Value.TotalHours <= source.MaxAgeHours));
                return new SourceStatus(source.Code, source.Name, source.Url, source.ActiveImport?.RecordCount, source.ActiveImport?.CompletedAt, source.LastCheckedAt, age, source.MaxAgeHours, current, last?.Status, last?.Message, last?.Status == ImportStatus.PendingReview ? last.Id : null);
            }),
        ];
    }

    /// <summary>The canonical hash: records sorted by external id, each with its content in a fixed order, so a file
    /// regenerated with a new date but the same records hashes the same.</summary>
    public static string CanonicalHash(IEnumerable<Subject> subjects)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var subject in subjects.OrderBy(s => s.ExternalId, StringComparer.Ordinal))
        {
            var builder = new StringBuilder();
            builder.AppendJoin('\u001f', subject.ExternalId, subject.Type, subject.PrimaryName, subject.Programmes, subject.ListedOn?.ToString("O", CultureInfo.InvariantCulture), subject.Remarks, subject.SourceUrl);
            foreach (var name in subject.Names.OrderBy(n => n.FullName, StringComparer.Ordinal).ThenBy(n => n.IsWeak))
            {
                builder.Append('\u001e').AppendJoin('\u001f', name.FullName, name.IsWeak, name.Script);
            }

            foreach (var address in subject.Addresses.OrderBy(a => a.Raw, StringComparer.Ordinal).ThenBy(a => a.CountryCode, StringComparer.Ordinal))
            {
                builder.Append('\u001d').AppendJoin('\u001f', address.Raw, address.CountryCode);
            }

            builder.Append('\n');
            sha.AppendData(Encoding.UTF8.GetBytes(builder.ToString()));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>One record per external id, independent of the file's order: of a repeated id's records, the base is
    /// the one that sorts first by its fields (primary name, programmes, listed on, remarks, source URL); the others'
    /// names and addresses join it. A spelling stays strong if any listing is strong; an address differing only by its
    /// country code is kept. Ids keep their first appearance's order.</summary>
    public static List<Subject> Deduplicate(IEnumerable<Subject> subjects)
    {
        var result = new List<Subject>();
        foreach (var group in subjects.GroupBy(subject => subject.ExternalId, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(subject => subject.PrimaryName, StringComparer.Ordinal)
                .ThenBy(subject => subject.Programmes, StringComparer.Ordinal)
                .ThenBy(subject => subject.ListedOn)
                .ThenBy(subject => subject.Remarks, StringComparer.Ordinal)
                .ThenBy(subject => subject.SourceUrl, StringComparer.Ordinal)
                .ThenBy(subject => subject.Type)
                .ToList();
            var first = ordered[0];
            foreach (var subject in ordered.Skip(1))
            {
                foreach (var name in subject.Names)
                {
                    var existing = first.Names.FirstOrDefault(n => n.FullName == name.FullName);
                    if (existing is null)
                    {
                        first.Names.Add(name);
                    }
                    else
                    {
                        existing.IsWeak &= name.IsWeak;
                    }
                }

                first.Addresses.AddRange(subject.Addresses.Where(address => !first.Addresses.Any(existing => existing.Raw == address.Raw && existing.CountryCode == address.CountryCode)));
            }

            result.Add(first);
        }

        return result;
    }
    private static Subject Detach(Subject subject)
    {
        var copy = new Subject
        {
            ExternalId = subject.ExternalId,
            Type = subject.Type,
            PrimaryName = subject.PrimaryName,
            Programmes = subject.Programmes,
            ListedOn = subject.ListedOn,
            Remarks = subject.Remarks,
            SourceUrl = subject.SourceUrl,
        };
        copy.Names.AddRange(subject.Names.Select(n => new SubjectName { FullName = n.FullName, IsWeak = n.IsWeak, Script = n.Script }));
        copy.Addresses.AddRange(subject.Addresses.Select(a => new SubjectAddress { Raw = a.Raw, CountryCode = a.CountryCode }));
        return copy;
    }

    private SqlConnection Connection => (SqlConnection)context.Database.GetDbConnection();

    private async Task WriteSubjectsAsync(Source source, Import import, List<Subject> records, CancellationToken cancellationToken)
    {
        foreach (var batch in records.Chunk(BatchSize))
        {
            var table = new DataTable();
            table.Columns.Add(nameof(Subject.ImportId), typeof(int));
            table.Columns.Add(nameof(Subject.SourceId), typeof(int));
            table.Columns.Add(nameof(Subject.ExternalId), typeof(string));
            table.Columns.Add(nameof(Subject.Type), typeof(byte));
            table.Columns.Add(nameof(Subject.PrimaryName), typeof(string));
            table.Columns.Add(nameof(Subject.Programmes), typeof(string));
            table.Columns.Add(nameof(Subject.ListedOn), typeof(DateTime));
            table.Columns.Add(nameof(Subject.Remarks), typeof(string));
            table.Columns.Add(nameof(Subject.SourceUrl), typeof(string));
            foreach (var subject in batch)
            {
                table.Rows.Add(import.Id, source.Id, subject.ExternalId, (byte)subject.Type, Truncate(subject.PrimaryName, 500), Truncate(subject.Programmes, 1000),
                    subject.ListedOn?.ToDateTime(TimeOnly.MinValue) is { } date ? date : DBNull.Value, subject.Remarks, Truncate(subject.SourceUrl, 1000));
            }

            await BulkCopyAsync("Subjects", table, cancellationToken);
        }

        var ids = await context.Subjects.AsNoTracking().Where(s => s.ImportId == import.Id)
            .Select(s => new { s.Id, s.ExternalId }).ToDictionaryAsync(s => s.ExternalId, s => s.Id, StringComparer.Ordinal, cancellationToken);

        var names = records.SelectMany(subject => subject.Names.Select(name => (Id: ids[subject.ExternalId], Name: name)));
        foreach (var batch in names.Chunk(BatchSize))
        {
            var table = new DataTable();
            table.Columns.Add(nameof(SubjectName.SubjectId), typeof(int));
            table.Columns.Add(nameof(SubjectName.FullName), typeof(string));
            table.Columns.Add(nameof(SubjectName.IsWeak), typeof(bool));
            table.Columns.Add(nameof(SubjectName.Script), typeof(byte));
            foreach (var (id, name) in batch)
            {
                table.Rows.Add(id, Truncate(name.FullName, 500), name.IsWeak, (byte)name.Script);
            }

            await BulkCopyAsync("SubjectNames", table, cancellationToken);
        }

        var addresses = records.SelectMany(subject => subject.Addresses.Select(address => (Id: ids[subject.ExternalId], Address: address)));
        foreach (var batch in addresses.Chunk(BatchSize))
        {
            var table = new DataTable();
            table.Columns.Add(nameof(SubjectAddress.SubjectId), typeof(int));
            table.Columns.Add(nameof(SubjectAddress.Raw), typeof(string));
            table.Columns.Add(nameof(SubjectAddress.CountryCode), typeof(string));
            foreach (var (id, address) in batch)
            {
                table.Rows.Add(id, Truncate(address.Raw, 1000), (object?)address.CountryCode ?? DBNull.Value);
            }

            await BulkCopyAsync("SubjectAddresses", table, cancellationToken);
        }
    }

    private async Task BulkCopyAsync(string tableName, DataTable table, CancellationToken cancellationToken)
    {
        using var bulkCopy = new SqlBulkCopy(Connection) { DestinationTableName = tableName, BatchSize = BatchSize, BulkCopyTimeout = 600 };
        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, cancellationToken);
    }

    /// <summary>The flip, in one transaction: the source moves to the new import only if its active import is still
    /// the one this import started from, and the import completes. The condition, not the application lock, is
    /// what guarantees no lost update: a retried command may run on a new session that no longer holds the lock.</summary>
    private async Task ActivateAsync(Source source, Import import, int? expectedActive, DateTimeOffset checkedAt, ReviewStamp? review, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var flipped = await context.Sources
                .Where(s => s.Id == source.Id && s.ActiveImportId == expectedActive)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.ActiveImportId, import.Id).SetProperty(s => s.LastCheckedAt, checkedAt), cancellationToken);
            if (flipped == 0)
            {
                throw new InvalidOperationException($"Another import of {source.Code} became active meanwhile; nothing was changed. Run it again.");
            }

            await context.Imports.Where(i => i.Id == import.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.Status, ImportStatus.Completed).SetProperty(i => i.CompletedAt, now), cancellationToken);
            if (review is { } stamp)
            {
                await context.Imports.Where(i => i.Id == import.Id).ExecuteUpdateAsync(
                    set => set.SetProperty(i => i.ReviewedBy, stamp.Reviewer).SetProperty(i => i.ReviewedAt, stamp.At).SetProperty(i => i.Message, stamp.Message), cancellationToken);
            }

            // Any other hold is moot once a list becomes active: superseded in the same transaction, records later.
            await SupersedeHeldAsync(source.Id, import.Id, $"superseded by import {import.Id}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        // The tracked entities take what was committed (the update bypassed the change tracker).
        await context.Entry(source).ReloadAsync(cancellationToken);
        await context.Entry(import).ReloadAsync(cancellationToken);
    }

    /// <summary>Keeps the records of the active import, the one before it, and any held for review; deletes every
    /// other import's records. Every import's row stays, as the history.</summary>
    private async Task DeleteOldImportsAsync(Source source, CancellationToken cancellationToken)
    {
        var previous = await context.Imports.Where(i => i.SourceId == source.Id && i.Status == ImportStatus.Completed && i.Id != source.ActiveImportId)
            .OrderByDescending(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(cancellationToken);
        var old = await context.Imports.Where(i => i.SourceId == source.Id && i.HasData && i.Id != source.ActiveImportId && i.Id != previous && i.Status != ImportStatus.PendingReview)
            .Select(i => i.Id).ToListAsync(cancellationToken);
        foreach (var id in old)
        {
            await DeleteImportDataAsync(id, cancellationToken);
        }

        await context.Imports.Where(i => old.Contains(i.Id)).ExecuteUpdateAsync(set => set.SetProperty(i => i.HasData, false), cancellationToken);
    }

    /// <summary>Marks the source's other held imports Rejected (one statement, so it can join a transaction). Their
    /// records go with the next cleanup (<see cref="DeleteOldImportsAsync"/>).</summary>
    private async Task SupersedeHeldAsync(int sourceId, int? exceptId, string reason, CancellationToken cancellationToken) =>
        await context.Imports.Where(i => i.SourceId == sourceId && i.Status == ImportStatus.PendingReview && i.Id != exceptId)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.Status, ImportStatus.Rejected).SetProperty(i => i.Message, i => ((i.Message ?? string.Empty) + "; " + reason).Substring(0, 1000)), cancellationToken);

    /// <summary>Rejected first, then its records deleted: an interrupted deletion leaves a rejected import (cleaned up
    /// later), never a held one that could still be confirmed with part of its records.</summary>
    private async Task RejectHeldAsync(Import import, string reason, DateTimeOffset now, string? reviewer, CancellationToken cancellationToken)
    {
        import.Status = ImportStatus.Rejected;
        import.Message = Truncate($"{import.Message}; {reason}", 1000);
        if (reviewer is not null)
        {
            import.ReviewedAt = now;
            import.ReviewedBy = Truncate(reviewer, 256);
        }

        await context.SaveChangesAsync(cancellationToken);
        await DeleteImportDataAsync(import.Id, cancellationToken);
        import.HasData = false;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await work();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>Deletes an import's names, addresses and subjects in chunks, so a 5 DTU database isn't held by one
    /// long statement.</summary>
    private async Task DeleteImportDataAsync(int importId, CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
        {
            "DELETE TOP (20000) n FROM SubjectNames n JOIN Subjects s ON s.Id = n.SubjectId WHERE s.ImportId = @import",
            "DELETE TOP (20000) a FROM SubjectAddresses a JOIN Subjects s ON s.Id = a.SubjectId WHERE s.ImportId = @import",
            "DELETE TOP (20000) FROM Subjects WHERE ImportId = @import",
        })
        {
            while (await context.Database.ExecuteSqlRawAsync(sql, [new SqlParameter("@import", importId)], cancellationToken) > 0)
            {
            }
        }
    }

    private async Task AcquireLockAsync(string code, CancellationToken cancellationToken)
    {
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0",
            [result, new SqlParameter("@resource", "import:" + code)],
            cancellationToken);
        if ((int)result.Value < 0)
        {
            throw new InvalidOperationException($"Another import of {code} is running.");
        }
    }

    private async Task ReleaseLockAsync(string code)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                "IF APPLOCK_MODE('public', @resource, 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'",
                [new SqlParameter("@resource", "import:" + code)]);
        }
        catch (SqlException)
        {
            // The lock is the session's: closing the connection releases it anyway.
        }
    }

    private static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}

/// <summary>A source as <c>status</c> reports it. <see cref="Current"/>: it has an active import and its last
/// successful check is within its allowed age (manual sources never age).</summary>
public sealed record SourceStatus(
    string Code,
    string Name,
    string? Url,
    int? RecordCount,
    DateTimeOffset? ImportedAt,
    DateTimeOffset? LastCheckedAt,
    TimeSpan? Age,
    int? MaxAgeHours,
    bool Current,
    ImportStatus? LastImportStatus,
    string? LastImportMessage,
    int? HeldImportId);

/// <summary>Who confirmed a held import, when, and its message after: written with the activation.</summary>
internal sealed record ReviewStamp(string Reviewer, DateTimeOffset At, string Message);

/// <summary>How an import treats "unchanged" and the drop guard.</summary>
internal enum ImportMode
{
    /// <summary>A feed refresh: unchanged moves <see cref="Source.LastCheckedAt"/>; the drop guard applies.</summary>
    Feed,

    /// <summary><c>--force</c>: imported even if unchanged, past the drop guard.</summary>
    Forced,

    /// <summary>A manual edit: unchanged writes nothing; no drop guard.</summary>
    Manual,
}