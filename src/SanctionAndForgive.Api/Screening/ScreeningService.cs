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

    /// <summary>Screens a valid request against the current lists and logs the result under its reference.</summary>
    public ScreeningResponse Screen(ScreeningRequestBody body)
    {
        var now = clock.GetUtcNow();
        var lists = catalog.Lists;
        var screened = lists.Select(list => new ScreenedList(list.Code, list.Name, list.LastCheckedAt, list.IsCurrent(now) ? list.Index : null)).ToList();
        var result = screener.Screen(new ScreeningRequest(body.ReceiverName, body.CompanyName, body.Address), screened);

        // Rows with hits first (best percentage across the entries), then the others in their usual order.
        var best = result.Entries.SelectMany(entry => entry.Cells).GroupBy(cell => cell.List).ToDictionary(group => group.Key, group => group.Max(cell => cell.Percent ?? -1));
        var rows = lists.Select(list => ListResponse.From(list, now))
            .OrderByDescending(row => best.GetValueOrDefault(row.Code, -1) >= options.ReviewMinPercent ? best[row.Code] : -1)
            .ToList();

        Audit(body.Reference!, result, rows);
        return new ScreeningResponse(
            body.Reference!,
            result.Outcome.ToString(),
            Summary(result, lists, now),
            ScreeningResponse.DisclaimerText,
            result.MatcherVersion,
            rows,
            [.. result.Entries.Select(entry => EntryResponse.From(entry, options))]);
    }

    /// <summary>The audit line: the reference, the outcome, the versions that produced it, and per entry kind and list
    /// the outcome, percentage and the best candidate's id on that list. Searchable by reference in the logs
    /// (docs/api.md, "Finding a screening in the logs"). The entered text never appears.</summary>
    private void Audit(string reference, ScreeningResult result, List<ListResponse> rows)
    {
        var lists = string.Join(", ", rows.Select(row => $"{row.Code}@{row.Version?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
        var results = new StringBuilder();
        foreach (var entry in result.Entries)
        {
            results.Append(entry.Kind).Append(": ");
            results.AppendJoin("; ", entry.Cells.Select(cell => cell.Checked
                ? string.Create(CultureInfo.InvariantCulture, $"{cell.List} {cell.Outcome} {cell.Percent}%{(cell.Best is { } match && match.Percent >= options.BestCandidateMinPercent ? $" [{match.Subject.ExternalId}]" : string.Empty)}")
                : $"{cell.List} NotChecked"));
            results.Append(" | ");
        }

        logger.LogInformation("Screening {Reference}: {Outcome}; matcher {MatcherVersion}; lists {ListVersions}; {Results}", reference, result.Outcome, result.MatcherVersion, lists, results.ToString().TrimEnd(' ', '|'));
    }

    /// <summary>The summary line, worded precisely: never "not sanctioned" or "cleared", always which lists, as of when.</summary>
    private static string Summary(ScreeningResult result, IReadOnlyList<LoadedList> lists, DateTimeOffset now)
    {
        var checkedLists = lists.Where(list => list.IsCurrent(now)).Select(list => $"{list.Name}, as of {AsOf(list.LastCheckedAt)}").ToList();
        var notChecked = lists.Where(list => !list.IsCurrent(now)).Select(list => list.Name).ToList();
        var gap = notChecked.Count > 0 ? $" Not checked: {string.Join(", ", notChecked)}." : string.Empty;
        var hits = result.Entries.SelectMany(entry => entry.Cells).Where(cell => cell.Outcome is Outcome.Match or Outcome.Review)
            .Select(cell => lists.First(list => list.Code == cell.List).Name).Distinct().ToList();

        return result.Outcome switch
        {
            OverallOutcome.Match => $"Possible match on {string.Join(", ", hits)}. A person must review it before anything else happens.{gap}",
            OverallOutcome.Review => $"Review needed: a similar listed party on {string.Join(", ", hits)}.{gap}",
            OverallOutcome.Incomplete when checkedLists.Count == 0 => $"Incomplete: no list could be checked.{gap}",
            OverallOutcome.Incomplete => $"Incomplete. No match found in: {string.Join("; ", checkedLists)}.{gap}",
            _ => $"No match found in: {string.Join("; ", checkedLists)}.",
        };
    }

    private static string AsOf(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "unknown";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
