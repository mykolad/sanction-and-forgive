using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SanctionAndForgive.Api.Controllers;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Screening;

/// <summary>One screening, as both the single and the streaming endpoint run it: validation, matching against the
/// lists in memory (never the database), the response, and the audit log line. The log line carries the caller's
/// reference and what each list answered, never what was entered: names and addresses can be personal data.</summary>
public sealed partial class ScreeningService(ListCatalog catalog, Screener screener, MatchingOptions options, TimeProvider clock, ILogger<ScreeningService> logger)
{
    public const int MaxFieldLength = 300;

    public bool IsReady => catalog.IsReady;

    /// <summary>What's wrong with a request, by field (empty when it's valid). Never echoes a value.</summary>
    public static Dictionary<string, string[]> Validate(ScreeningRequestBody body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body.Reference))
        {
            errors[nameof(body.Reference)] = ["A reference is required: your own id for this screening (a transaction id, say), to find it in the logs."];
        }
        else if (!ReferencePattern().IsMatch(body.Reference))
        {
            errors[nameof(body.Reference)] = ["A reference is 1-64 characters: letters, digits and . _ : / -, starting with a letter or digit. Never put personal data in it."];
        }

        foreach (var (name, value) in new[] { (nameof(body.ReceiverName), body.ReceiverName), (nameof(body.CompanyName), body.CompanyName), (nameof(body.Address), body.Address) })
        {
            if (value?.Length > MaxFieldLength)
            {
                errors[name] = [$"At most {MaxFieldLength} characters."];
            }
        }

        if (string.IsNullOrWhiteSpace(body.ReceiverName) && string.IsNullOrWhiteSpace(body.CompanyName) && string.IsNullOrWhiteSpace(body.Address))
        {
            errors[string.Empty] = ["Enter a receiver, a company or an address."];
        }

        return errors;
    }

    /// <summary>The lists as they are now: a stream screens every record against one snapshot.</summary>
    public IReadOnlyList<LoadedList> Snapshot => catalog.Lists;

    public DateTimeOffset Now => clock.GetUtcNow();

    public MatchingOptions Options => options;

    public string MatcherVersion => screener.MatcherVersion;

    /// <summary>The allow-list as it is now: a stream screens every record against one.</summary>
    public AllowList AllowList => catalog.AllowList;

    /// <summary>Screens a valid request against the current lists and logs the result under its reference.</summary>
    public ScreeningResponse Screen(ScreeningRequestBody body) => Screen(body, catalog.Lists, catalog.AllowList);

    /// <summary>Screens a valid request against <paramref name="lists"/> (a snapshot) and logs the result.</summary>
    public ScreeningResponse Screen(ScreeningRequestBody body, IReadOnlyList<LoadedList> lists, AllowList allowList)
    {
        var (result, rows, now) = Run(body, lists, allowList);
        return new ScreeningResponse(
            body.Reference!,
            result.Outcome.ToString(),
            Summary(result, lists, now),
            ScreeningResponse.DisclaimerText,
            result.MatcherVersion,
            rows,
            [.. result.Entries.Select(entry => EntryResponse.From(entry, options))])
        {
            AllowListVersion = result.AllowListVersion,
        };
    }

    /// <summary>The screening and its audit line, for a caller that shapes its own response (the stream).</summary>
    public ScreeningResult ScreenForStream(ScreeningRequestBody body, IReadOnlyList<LoadedList> lists, AllowList allowList) => Run(body, lists, allowList).Result;

    private (ScreeningResult Result, List<ListResponse> Rows, DateTimeOffset Now) Run(ScreeningRequestBody body, IReadOnlyList<LoadedList> lists, AllowList allowList)
    {
        var now = clock.GetUtcNow();
        // A list an administrator switched off isn't searched at all: no cells, so it never makes a result Incomplete.
        var screened = lists.Where(list => list.SwitchedOff is null).Select(list => new ScreenedList(list.Code, list.Name, list.LastCheckedAt, list.Index)).ToList();
        var result = screener.Screen(new ScreeningRequest(body.ReceiverName, body.CompanyName, body.Address), screened, allowList);

        // Rows with hits first (best percentage across the entries), then the others in their usual order.
        var best = result.Entries.SelectMany(entry => entry.Cells).GroupBy(cell => cell.List).ToDictionary(group => group.Key, group => group.Max(cell => cell.Percent ?? -1));
        var rows = lists.Select(list => ListResponse.From(list, now))
            .OrderByDescending(row => best.GetValueOrDefault(row.Code, -1) >= options.ReviewMinPercent ? best[row.Code] : -1)
            .ToList();

        Audit(body.Reference!, result, rows);
        return (result, rows, now);
    }

    /// <summary>The audit line: the reference, the outcome, the versions that produced it, and per entry kind and list
    /// the outcome, percentage and the best candidate's id on that list. Searchable by reference in the logs
    /// (docs/api.md, "Finding a screening in the logs"). The entered text never appears.</summary>
    private void Audit(string reference, ScreeningResult result, List<ListResponse> rows)
    {
        var lists = string.Join(", ", rows.Select(row => $"{row.Code}@{row.Version?.ToString(CultureInfo.InvariantCulture) ?? "-"}{row.State switch { "Overdue" => " overdue", "SwitchedOff" => " off", _ => string.Empty }}"));
        var results = new StringBuilder();
        foreach (var entry in result.Entries)
        {
            results.Append(entry.Kind).Append(entry.AllowedBy is { } allowed ? string.Create(CultureInfo.InvariantCulture, $" (allow-list entry {allowed.Id})") : string.Empty).Append(": ");
            results.AppendJoin("; ", entry.Cells.Select(cell => cell.Checked
                ? string.Create(CultureInfo.InvariantCulture, $"{cell.List} {cell.Outcome}{(cell.Allowed ? " allowed" : string.Empty)} {cell.Percent}%{(cell.Best is { } match && match.Percent >= options.BestCandidateMinPercent ? $" [{match.Subject.ExternalId}]" : string.Empty)}")
                : $"{cell.List} NotChecked"));
            results.Append(" | ");
        }

        logger.LogInformation("Screening {Reference}: {Outcome}; matcher {MatcherVersion}; lists {ListVersions}; allow-list {AllowListVersion}; {Results}", reference, result.Outcome, result.MatcherVersion, lists, result.AllowListVersion, results.ToString().TrimEnd(' ', '|'));
    }

    /// <summary>The summary line, worded precisely: never "not sanctioned" or "cleared", always which lists, as of when,
    /// and a warning for each list searched although it couldn't be updated within its allowed age.</summary>
    private static string Summary(ScreeningResult result, IReadOnlyList<LoadedList> lists, DateTimeOffset now)
    {
        var checkedLists = lists.Where(list => list.IsSearched).Select(list => $"{list.Name}, as of {AsOf(list.LastCheckedAt)}").ToList();
        var notChecked = lists.Where(list => list.SwitchedOff is null && !list.IsSearched).Select(list => list.Name).ToList();
        var switchedOff = lists.Where(list => list.SwitchedOff is not null).Select(list => $"{list.Name} (switched off by an administrator: {list.SwitchedOff!.Reason})").ToList();
        var overdue = lists.Where(list => list.IsOverdue(now))
            .Select(list => string.Create(CultureInfo.InvariantCulture, $"{list.Name} couldn't be updated within {list.MaxAgeHours} hours and was searched as of {AsOf(list.LastCheckedAt)}"))
            .ToList();
        var gap = (notChecked.Count > 0 ? $" Not checked: {string.Join(", ", notChecked)}." : string.Empty)
            + (switchedOff.Count > 0 ? $" Not searched: {string.Join("; ", switchedOff)}." : string.Empty)
            + (overdue.Count > 0 ? $" Warning: {string.Join("; ", overdue)}." : string.Empty);
        var hits = result.Entries.SelectMany(entry => entry.Cells).Where(cell => cell.Outcome is Outcome.Match or Outcome.Review && !cell.Allowed)
            .Select(cell => lists.First(list => list.Code == cell.List).Name).Distinct().ToList();
        var allowedOn = result.Entries.SelectMany(entry => entry.Cells).Where(cell => cell.Allowed)
            .Select(cell => lists.First(list => list.Code == cell.List).Name).Distinct().ToList();
        var allowed = allowedOn.Count > 0
            ? $" A possible match on {string.Join(", ", allowedOn)} is allowed by the allow-list ({string.Join("; ", result.Entries.Where(entry => entry.AllowedBy is not null && entry.Cells.Any(cell => cell.Allowed)).Select(entry => $"{entry.AllowedBy!.Name}: {entry.AllowedBy.Reason}"))}); a person can still review it."
            : string.Empty;

        return result.Outcome switch
        {
            OverallOutcome.Match => $"Possible match on {string.Join(", ", hits)}. A person must review it before anything else happens.{gap}{allowed}",
            OverallOutcome.Review => $"Review needed: a similar listed party on {string.Join(", ", hits)}.{gap}{allowed}",
            OverallOutcome.Incomplete when checkedLists.Count == 0 => $"Incomplete: no list could be checked.{gap}",
            OverallOutcome.Incomplete => $"Incomplete. No match found in: {string.Join("; ", checkedLists)}.{gap}{allowed}",
            _ when allowed.Length > 0 => $"No match found in: {string.Join("; ", checkedLists)}, apart from what the allow-list allows.{gap}{allowed}",
            _ => $"No match found in: {string.Join("; ", checkedLists)}.{gap}",
        };
    }

    private static string AsOf(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "unknown";

    // \z, not $: $ also matches before a final newline, so "TX-1\n" would pass.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
