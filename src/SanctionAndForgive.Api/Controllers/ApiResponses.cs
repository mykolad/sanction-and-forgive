using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Controllers;

// Every API response is a record here, never an entity or an anonymous object: a field isn't public until it's added
// here. Nothing the user entered is stored or logged; it only comes back in the response that answers it.

/// <param name="Reference">The caller's own id for this screening (a transaction id, say): required, 1-64 characters
/// of letters, digits and <c>. _ : / -</c>. It's echoed back and logged with the results, so a screening can be found
/// later; never put personal data in it.</param>
public sealed record ScreeningRequestBody(string? Reference, string? ReceiverName, string? CompanyName, string? Address);

public sealed record ScreeningResponse(
    string Reference,
    string Outcome,
    string Summary,
    string Disclaimer,
    string MatcherVersion,
    IReadOnlyList<ListResponse> Lists,
    IReadOnlyList<EntryResponse> Entries)
{
    public const string DisclaimerText =
        "This is a demo list check, not a legal determination. It doesn't cover ownership or control by listed parties, "
        + "or embargoes. A person must review possible matches. The percentage is an estimate that this entry is the "
        + "listed party, based on the data entered; it isn't the chance that a shipment is lawful.";
}

/// <param name="State"><c>Current</c>, or <c>NotChecked</c> (failed to load, older than allowed, or no index).</param>
/// <param name="AsOf">The list's last successful check.</param>
/// <param name="Version">The import the answer was computed from (immutable: a changed list is a new import).</param>
public sealed record ListResponse(string Code, string Name, DateTimeOffset? AsOf, string State, int? Version, int RecordCount, string? Url)
{
    public static ListResponse From(LoadedList list, DateTimeOffset now) =>
        new(list.Code, list.Name, list.LastCheckedAt, list.IsCurrent(now) ? "Current" : "NotChecked", list.Index is null ? null : list.ActiveImportId, list.RecordCount, SanctionAndForgive.Lists.ListSources.Find(list.Code)?.PublicUrl ?? list.Url);
}

public sealed record EntryResponse(string Kind, string Input, string Compared, IReadOnlyList<CellResponse> Cells)
{
    public static EntryResponse From(Entry entry, MatchingOptions options) =>
        new(entry.Kind.ToString(), entry.Input, entry.Compared, [.. entry.Cells.Select(cell => CellResponse.From(cell, options))]);
}

/// <param name="State"><c>Checked</c> or <c>NotChecked</c>. Not checked: no percentage, no outcome.</param>
/// <param name="BestCandidate">Shown from <see cref="MatchingOptions.BestCandidateMinPercent"/> (20%).</param>
public sealed record CellResponse(string List, string State, int? Percent, string? Outcome, CandidateResponse? BestCandidate, IReadOnlyList<CandidateResponse> OtherHits)
{
    public static CellResponse From(Cell cell, MatchingOptions options) => new(
        cell.List,
        cell.Checked ? "Checked" : "NotChecked",
        cell.Percent,
        cell.Outcome?.ToString(),
        cell.Best is { } best && best.Percent >= options.BestCandidateMinPercent ? CandidateResponse.From(best) : null,
        [.. cell.Others.Select(CandidateResponse.From)]);
}

public sealed record CandidateResponse(
    string ListedName,
    string Alias,
    string? Programme,
    string? SourceUrl,
    string ExternalId,
    string Type,
    int Percent,
    string Outcome,
    IReadOnlyList<string> Alignment,
    string AddressState,
    IReadOnlyList<string> ListedAddresses)
{
    public static CandidateResponse From(SubjectMatch match) => new(
        match.Subject.PrimaryName,
        match.Alias,
        match.Subject.Programme,
        match.Subject.SourceUrl,
        match.Subject.ExternalId,
        match.Subject.Kind.ToString(),
        match.Percent,
        match.Outcome.ToString(),
        match.Alignment,
        match.AddressState.ToString(),
        [.. match.Subject.Addresses.Select(address => address.CountryCode is { } country && !address.Raw.Contains(country, StringComparison.Ordinal) ? $"{address.Raw} ({country})" : address.Raw)]);
}

public sealed record StatusResponse(string State, string Version, string MatcherVersion, IReadOnlyList<ListResponse> Lists);

public sealed record WarmingUpResponse(string State);

public sealed record ExampleResponse(string Label, string? ReceiverName, string? CompanyName, string? Address, string List);

public sealed record HealthResponse(string Status, string Version, string? Revision);
