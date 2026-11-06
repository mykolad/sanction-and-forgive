using System.Globalization;
using SanctionAndForgive.Data;
using SanctionAndForgive.Lists;

namespace SanctionAndForgive.Cli;

/// <summary>The list commands behind <c>tools/lists.ps1</c>: import feeds, report status, and manage manual
/// sources. Every change goes through <see cref="ImportStore"/>, so a manual change is a new import too and readers
/// never see half of it. Writes list data only, never anything a user screened.</summary>
internal sealed class ListCommands(ImportStore store, FeedDownloader downloader, string euToken, TextWriter output)
{
    /// <summary><c>import --source all</c> imports every active feed; one named source is imported even if its terms
    /// still wait for a decision (naming it is the decision). One failed source doesn't stop the others, but the run
    /// then exits 1, and so does a rejected one.</summary>
    public async Task<int> ImportAsync(string sourceCode, string? file, bool force, CancellationToken cancellationToken)
    {
        IReadOnlyList<ListSource> sources;
        if (sourceCode == "all")
        {
            if (file is not null)
            {
                output.WriteLine("--file needs a single --source: one file is one feed.");
                return 2;
            }

            sources = [.. ListSources.All.Where(source => source.Status == FeedStatus.Active)];
        }
        else if (ListSources.Find(sourceCode) is { } source)
        {
            sources = [source];
        }
        else
        {
            output.WriteLine($"{sourceCode} is not a feed. Manual sources change with add and remove.");
            return 2;
        }

        var failed = false;
        foreach (var source in sources)
        {
            failed |= !await ImportOneAsync(source, file, force, cancellationToken);
        }

        return failed ? 1 : 0;
    }

    public async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var statuses = await store.StatusAsync(cancellationToken);
        output.WriteLine("source       records  imported (UTC)     last checked (UTC)  age     max   state         last import");
        foreach (var status in statuses)
        {
            var age = status.Age is { } a ? $"{a.TotalHours,5:N1}h" : "     -";
            var maxAge = status.MaxAgeHours is { } m ? $"{m}h" : "-";
            var state = status.Current ? "Current" : status.RecordCount is null ? "Not imported" : "Not checked";
            var last = status.LastImportStatus is { } lastStatus ? $"{lastStatus}{(status.LastImportMessage is { } message ? ": " + message : string.Empty)}" : "-";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{status.Code,-10} {status.RecordCount,9:N0}  {status.ImportedAt,-18:yyyy-MM-dd HH:mm}  {status.LastCheckedAt,-18:yyyy-MM-dd HH:mm}  {age}  {maxAge,4}  {state,-12}  {last}"));
        }

        var known = statuses.Select(status => status.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var feed in ListSources.All.Where(feed => !known.Contains(feed.Code)))
        {
            output.WriteLine($"{feed.Code,-10}         -  never imported ({feed.Status})");
        }

        return 0;
    }

    public async Task<int> SourceAddAsync(string code, string name, CancellationToken cancellationToken)
    {
        if (ListSources.Find(code) is not null)
        {
            output.WriteLine($"{code} is a feed's code; pick another for a manual source.");
            return 2;
        }

        await store.EnsureSourceAsync(code, name, url: null, maxAgeHours: null, cancellationToken);
        output.WriteLine($"Manual source {code} ({name}) is ready. Add records with: add -Source {code} -File records.csv");
        return 0;
    }

    /// <summary>Upserts the CSV's records by external id into a manual source's active list, under the source's lock.</summary>
    public async Task<int> AddAsync(string code, string file, CancellationToken cancellationToken)
    {
        if (await ManualSourceAsync(code, cancellationToken) is not { } source)
        {
            return 2;
        }

        List<ParsedSubject> records;
        using (var reader = new StreamReader(file, System.Text.Encoding.UTF8))
        {
            records = ManualCsvReader.Read(reader);
        }

        var outcome = await store.ChangeAsync(source, current =>
        {
            var subjects = current.ToDictionary(subject => subject.ExternalId, StringComparer.Ordinal);
            foreach (var record in records)
            {
                subjects[record.ExternalId] = SubjectMapper.ToEntity(record);
            }

            return [.. subjects.Values];
        }, cancellationToken);
        return Report(code, $"{records.Count} records upserted", outcome);
    }

    public async Task<int> RemoveAsync(string code, string externalId, CancellationToken cancellationToken)
    {
        if (await ManualSourceAsync(code, cancellationToken) is not { } source)
        {
            return 2;
        }

        var found = false;
        var outcome = await store.ChangeAsync(source, current =>
        {
            found = current.RemoveAll(subject => subject.ExternalId == externalId) > 0;
            return current;
        }, cancellationToken);
        if (!found)
        {
            output.WriteLine($"{code} has no record {externalId}; nothing changed.");
            return 1;
        }

        return Report(code, $"removed {externalId}", outcome);
    }

    /// <summary>A manual change's result. A rejected change (the list would be empty) kept the old list: exit 1.</summary>
    private int Report(string code, string what, ImportOutcome outcome)
    {
        if (outcome.Result == ImportResult.Rejected)
        {
            output.WriteLine($"{code}: NOT changed: {outcome.Message}. A list can't be emptied: it keeps at least one record.");
            return 1;
        }

        output.WriteLine($"{code}: {what}, {outcome.RecordCount} in the list ({outcome.Result}).");
        return 0;
    }
    private async Task<bool> ImportOneAsync(ListSource source, string? file, bool force, CancellationToken cancellationToken)
    {
        if (source.CreateParser is null)
        {
            output.WriteLine($"{source.Code}: no parser yet ({source.Status}); see docs/sources.md.");
            return false;
        }

        string? path = file;
        Source? record = null;
        try
        {
            record = await store.EnsureSourceAsync(source.Code, source.Name, source.Url?.Replace("{token}", "…", StringComparison.Ordinal), source.MaxAgeHours, cancellationToken);
            path ??= await downloader.DownloadAsync(new Uri(source.Url!.Replace("{token}", euToken, StringComparison.Ordinal)), cancellationToken);
            List<SanctionAndForgive.Data.Subject> subjects;
            await using (var stream = File.OpenRead(path))
            {
                subjects = [.. source.CreateParser().Parse(stream, cancellationToken).Select(SubjectMapper.ToEntity)];
            }

            var outcome = await store.ImportAsync(record, subjects, force, cancellationToken);
            output.WriteLine($"{source.Code}: {outcome.Result}, {outcome.RecordCount} records (was {outcome.PreviousCount?.ToString(CultureInfo.InvariantCulture) ?? "none"}). {outcome.Message}");
            return outcome.Result != ImportResult.Rejected;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException or ListFormatException or System.Xml.XmlException
            or System.Text.Json.JsonException or Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.DbUpdateException
            or InvalidOperationException or ArgumentException)
        {
            output.WriteLine($"{source.Code}: FAILED: {exception.GetType().Name}: {exception.Message}");

            // Nothing of this source's attempt may reach the next source's save; then the failure is recorded, so
            // status shows it. The active list and its last check stay as they were.
            store.Reset();
            if (record is not null)
            {
                try
                {
                    if (await store.FindSourceAsync(source.Code, CancellationToken.None) is { } stored)
                    {
                        await store.RecordFailureAsync(stored, $"{exception.GetType().Name}: {exception.Message}", CancellationToken.None);
                    }
                }
                catch (Exception recordFailure) when (recordFailure is Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException)
                {
                    output.WriteLine($"{source.Code}: the failure couldn't be recorded either ({recordFailure.GetType().Name}).");
                    store.Reset();
                }
            }

            return false;
        }
        finally
        {
            if (file is null && path is not null)
            {
                File.Delete(path);
            }
        }
    }

    private async Task<Source?> ManualSourceAsync(string code, CancellationToken cancellationToken)
    {
        var source = await store.FindSourceAsync(code, cancellationToken);
        if (source is null || source.Url is not null || ListSources.Find(code) is not null)
        {
            output.WriteLine($"{code} is not a manual source. Create one with: source-add -Code {code} -Name \"…\"");
            return null;
        }

        return source;
    }
}

internal static class SubjectMapper
{
    public static SanctionAndForgive.Data.Subject ToEntity(ParsedSubject parsed)
    {
        var subject = new SanctionAndForgive.Data.Subject
        {
            ExternalId = parsed.ExternalId,
            Type = (SanctionAndForgive.Data.SubjectType)(byte)parsed.Type,
            PrimaryName = parsed.PrimaryName,
            Programmes = parsed.Programmes,
            ListedOn = parsed.ListedOn,
            Remarks = parsed.Remarks,
            SourceUrl = parsed.SourceUrl,
        };
        subject.Names.AddRange(parsed.Names.Select(name => new SubjectName { FullName = name.FullName, IsWeak = name.IsWeak, Script = (SanctionAndForgive.Data.NameScript)(byte)name.Script }));
        subject.Addresses.AddRange(parsed.Addresses.Select(address => new SubjectAddress { Raw = address.Raw, CountryCode = address.CountryCode }));
        return subject;
    }
}
