using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SanctionAndForgive.Data;

public enum ImportResult
{
    /// <summary>A new import became active.</summary>
    Imported,

    /// <summary>The content hash matched the active import: only <see cref="Source.LastCheckedAt"/> moved.</summary>
    Unchanged,

    /// <summary>The sanity guard kept the old list (zero records, or a drop of more than 20%).</summary>
    Rejected,
}

public sealed record ImportOutcome(ImportResult Result, int RecordCount, int? PreviousCount, string Message);

/// <summary>Writes imports (section 4, "Import rules"). Each import is a complete copy of a source's subjects, written
/// with <see cref="SqlBulkCopy"/> in batches, then made active in one transaction: readers never see half a list.
/// The active and the previous import are kept; older ones are deleted. One import per source runs at a time
/// (an application lock).</summary>
public sealed class ImportStore(ListsDbContext context, TimeProvider clock)
{
    private const int BatchSize = 5000;

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

    /// <summary>Imports <paramref name="subjects"/> as <paramref name="source"/>'s new list, unless it's unchanged or
    /// the sanity guard refuses it (<paramref name="force"/> overrides both).</summary>
    public async Task<ImportOutcome> ImportAsync(Source source, IReadOnlyList<Subject> subjects, bool force, CancellationToken cancellationToken)
    {
        var records = Deduplicate(subjects);
        var hash = CanonicalHash(records);
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await AcquireLockAsync(source.Code, cancellationToken);
            var active = source.ActiveImportId is { } activeId ? await context.Imports.SingleAsync(i => i.Id == activeId, cancellationToken) : null;
            var now = clock.GetUtcNow();

            if (!force && active?.ContentHash == hash)
            {
                source.LastCheckedAt = now;
                await context.SaveChangesAsync(cancellationToken);
                return new ImportOutcome(ImportResult.Unchanged, active.RecordCount, active.RecordCount, "Unchanged since the active import");
            }

            var refusal = records.Count == 0 ? "The file parsed to zero records"
                : !force && active is not null && records.Count < active.RecordCount * (1 - MaxDrop)
                    ? $"Records dropped from {active.RecordCount} to {records.Count}, more than {MaxDrop:P0}"
                    : null;
            if (refusal is not null)
            {
                context.Imports.Add(new Import { SourceId = source.Id, StartedAt = now, CompletedAt = now, ContentHash = hash, RecordCount = records.Count, Status = ImportStatus.Rejected, Message = refusal });
                await context.SaveChangesAsync(cancellationToken);
                return new ImportOutcome(ImportResult.Rejected, records.Count, active?.RecordCount, refusal + "; the active list is kept");
            }

            var import = new Import { SourceId = source.Id, StartedAt = now, ContentHash = hash, RecordCount = records.Count, Status = ImportStatus.Running };
            context.Imports.Add(import);
            await context.SaveChangesAsync(cancellationToken);
            try
            {
                await WriteSubjectsAsync(source, import, records, cancellationToken);
                await ActivateAsync(source, import, cancellationToken);
            }
            catch (Exception exception) when (exception is SqlException or DbUpdateException or InvalidOperationException or OperationCanceledException)
            {
                await DeleteImportDataAsync(import.Id, CancellationToken.None);
                import.Status = ImportStatus.Failed;
                import.Message = exception.GetType().Name;
                await context.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            await DeleteOldImportsAsync(source, cancellationToken);
            return new ImportOutcome(ImportResult.Imported, records.Count, active?.RecordCount, "Imported");
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
                return new SourceStatus(source.Code, source.Name, source.Url, source.ActiveImport?.RecordCount, source.ActiveImport?.CompletedAt, source.LastCheckedAt, age, source.MaxAgeHours, current, last?.Status, last?.Message);
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

    /// <summary>One record per external id: a repeated id's names and addresses join the first record.</summary>
    public static List<Subject> Deduplicate(IEnumerable<Subject> subjects)
    {
        var byId = new Dictionary<string, Subject>(StringComparer.Ordinal);
        var ordered = new List<Subject>();
        foreach (var subject in subjects)
        {
            if (byId.TryGetValue(subject.ExternalId, out var first))
            {
                first.Names.AddRange(subject.Names.Where(name => !first.Names.Any(existing => existing.FullName == name.FullName)));
                first.Addresses.AddRange(subject.Addresses.Where(address => !first.Addresses.Any(existing => existing.Raw == address.Raw)));
                continue;
            }

            byId[subject.ExternalId] = subject;
            ordered.Add(subject);
        }

        return ordered;
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
                table.Rows.Add(import.Id, source.Id, Truncate(subject.ExternalId, 200), (byte)subject.Type, Truncate(subject.PrimaryName, 500), Truncate(subject.Programmes, 1000),
                    subject.ListedOn?.ToDateTime(TimeOnly.MinValue) is { } date ? date : DBNull.Value, subject.Remarks, Truncate(subject.SourceUrl, 1000));
            }

            await BulkCopyAsync("Subjects", table, cancellationToken);
        }

        var ids = await context.Subjects.AsNoTracking().Where(s => s.ImportId == import.Id)
            .Select(s => new { s.Id, s.ExternalId }).ToDictionaryAsync(s => s.ExternalId, s => s.Id, StringComparer.Ordinal, cancellationToken);

        var names = records.SelectMany(subject => subject.Names.Select(name => (Id: ids[Truncate(subject.ExternalId, 200)!], Name: name)));
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

        var addresses = records.SelectMany(subject => subject.Addresses.Select(address => (Id: ids[Truncate(subject.ExternalId, 200)!], Address: address)));
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

    /// <summary>The flip: the new import completes and becomes active in one transaction (one SaveChanges is one
    /// transaction, and works with EF's retrying execution strategy, unlike a user-started transaction).</summary>
    private async Task ActivateAsync(Source source, Import import, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        import.Status = ImportStatus.Completed;
        import.CompletedAt = now;
        source.ActiveImportId = import.Id;
        source.LastCheckedAt = now;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Keeps the active import and the one before it; deletes every older import and its rows.</summary>
    private async Task DeleteOldImportsAsync(Source source, CancellationToken cancellationToken)
    {
        var previous = await context.Imports.Where(i => i.SourceId == source.Id && i.Status == ImportStatus.Completed && i.Id != source.ActiveImportId)
            .OrderByDescending(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(cancellationToken);
        var old = await context.Imports.Where(i => i.SourceId == source.Id && i.Id != source.ActiveImportId && i.Id != previous)
            .Select(i => i.Id).ToListAsync(cancellationToken);
        foreach (var id in old)
        {
            await DeleteImportDataAsync(id, cancellationToken);
        }

        await context.Imports.Where(i => old.Contains(i.Id)).ExecuteDeleteAsync(cancellationToken);
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
    string? LastImportMessage);
