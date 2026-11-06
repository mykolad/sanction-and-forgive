using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Controllers;

// The streaming endpoint's lines (NDJSON: one JSON object per line). Records here, like ApiResponses.cs: a field
// isn't public until it's added. Lines are compact on purpose: a batch can be tens of thousands of them, and the
// caller already has its input, keyed by reference, so nothing entered is echoed.

/// <summary>The first line: what every result in this stream was computed with.</summary>
public sealed record StreamHeaderLine(string Type, string MatcherVersion, string Disclaimer, IReadOnlyList<ListResponse> Lists, int MaxRecords, int MaxSeconds);

/// <summary>One record's result. <paramref name="Line"/> is its 1-based line number in the request.</summary>
public sealed record StreamResultLine(string Type, int Line, string Reference, string Outcome, IReadOnlyList<StreamEntry> Entries);

public sealed record StreamEntry(string Kind, IReadOnlyList<StreamCell> Cells)
{
    public static StreamEntry From(Entry entry, MatchingOptions options) =>
        new(entry.Kind.ToString(), [.. entry.Cells.Select(cell => StreamCell.From(cell, options))]);
}

/// <param name="State"><c>Checked</c> or <c>NotChecked</c>; Not checked has no percentage and no outcome.</param>
/// <param name="Best">The best candidate from 20%: enough to find it on the list.</param>
public sealed record StreamCell(string List, string State, int? Percent, string? Outcome, StreamCandidate? Best)
{
    public static StreamCell From(Cell cell, MatchingOptions options) => new(
        cell.List,
        cell.Checked ? "Checked" : "NotChecked",
        cell.Percent,
        cell.Outcome?.ToString(),
        cell.Best is { } best && best.Percent >= options.BestCandidateMinPercent
            ? new StreamCandidate(best.Subject.PrimaryName, best.Alias, best.Subject.ExternalId, best.Subject.SourceUrl)
            : null);
}

public sealed record StreamCandidate(string ListedName, string Alias, string ExternalId, string? SourceUrl);

/// <summary>A record that couldn't be screened: not valid JSON, too long, or invalid fields. The stream goes on.</summary>
public sealed record StreamErrorLine(string Type, int Line, string? Reference, IReadOnlyDictionary<string, string[]> Errors);

/// <summary>The last line: how many records were screened and how many were refused. <paramref name="Truncated"/>:
/// the stream reached its maximum records or seconds and stopped reading; send the request again from
/// <paramref name="NextLine"/> (1-based, the first line not read) in a new stream.</summary>
public sealed record StreamEndLine(string Type, int Screened, int Errors, bool Truncated, int? NextLine);
