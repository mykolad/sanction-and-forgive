using Microsoft.AspNetCore.Mvc;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;
using SanctionAndForgive.Lists;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>GET /api/v1/sources</c>, for the status page: every list the API searches, with its record count,
/// freshness and the publisher's page, then every catalogued feed that isn't imported, with the reason. Reads
/// memory and the catalogue, never SQL.</summary>
[ApiController]
[Route("api/v1/sources")]
public sealed class SourcesController(ListCatalog catalog, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SourcesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WarmingUpResponse>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Get()
    {
        if (!catalog.IsReady)
        {
            // Before the first load every feed would look "not imported": say it's loading instead.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new WarmingUpResponse("warmingUp"));
        }

        var now = clock.GetUtcNow();
        var loaded = catalog.Lists;
        var history = catalog.History;
        var searched = loaded.Select(list => SourceResponse.From(list, ListSources.Find(list.Code), now) with
        {
            Held = history.GetValueOrDefault(list.Code)?.FirstOrDefault(entry => entry.Status == ImportStatus.PendingReview) is { } held
                ? new HeldUpdateResponse(held.Id, held.RecordCount, held.StartedAt, held.Message)
                : null,
            History = [.. (history.GetValueOrDefault(list.Code) ?? []).Select(HistoryEntryResponse.From)],
        });
        var notImported = ListSources.All
            .Where(source => !loaded.Any(list => string.Equals(list.Code, source.Code, StringComparison.OrdinalIgnoreCase)))
            .Select(SourceResponse.NotImported);
        return Ok(new SourcesResponse(now, [.. searched, .. notImported]));
    }
}

public sealed record SourcesResponse(DateTimeOffset Now, IReadOnlyList<SourceResponse> Lists);

/// <param name="State"><c>Current</c> (searched), <c>NotChecked</c> (loaded but stale or not built: its cells say
/// Not checked), or <c>NotImported</c>.</param>
/// <param name="Status">The catalogue's <see cref="FeedStatus"/> (<c>Active</c>, <c>TermsUnclear</c>,
/// <c>NeedsAccess</c>), or <c>Manual</c> for a source maintained by hand.</param>
/// <param name="Reason">Why it isn't searched, when it isn't.</param>
/// <param name="LastCheckedAt">The last successful check, changed or not: freshness counts from it.</param>
/// <param name="UpdatedAt">When the list's content last changed here (its active import).</param>
/// <param name="Homepage">The publisher's page for the list; <paramref name="FileUrl"/> is the file itself.</param>
public sealed record SourceResponse(
    string Code,
    string Name,
    string State,
    string Status,
    string? Reason,
    int? RecordCount,
    int? Version,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? UpdatedAt,
    int? MaxAgeHours,
    string? Homepage,
    string? FileUrl,
    string? TermsUrl)
{
    /// <summary>An update held for review (a drop of more than 20%), if one is waiting: the list above still serves.</summary>
    public HeldUpdateResponse? Held { get; init; }

    /// <summary>The latest imports, newest first: changes, failures, held and rejected updates.</summary>
    public IReadOnlyList<HistoryEntryResponse> History { get; init; } = [];

    public static SourceResponse From(LoadedList list, ListSource? source, DateTimeOffset now)
    {
        var current = list.IsCurrent(now);
        string? reason = current ? null
            : list.ActiveImportId is null ? "No completed import yet."
            : list.Index is null ? "Loading, or its last load failed. Its cells say Not checked until it loads."
            : $"Not checked successfully for more than {list.MaxAgeHours} hours. Its cells say Not checked until a check succeeds.";
        return new(
            list.Code,
            list.Name,
            current ? "Current" : "NotChecked",
            source?.Status.ToString() ?? "Manual",
            reason,
            list.RecordCount,
            list.Index is null ? null : list.ActiveImportId,
            list.LastCheckedAt,
            list.UpdatedAt,
            list.MaxAgeHours,
            source?.Homepage,
            source?.PublicUrl,
            source?.TermsUrl);
    }

    public static SourceResponse NotImported(ListSource source) => new(
        source.Code,
        source.Name,
        "NotImported",
        source.Status.ToString(),
        source.Note ?? "Not imported yet.",
        RecordCount: null,
        Version: null,
        LastCheckedAt: null,
        UpdatedAt: null,
        source.MaxAgeHours,
        source.Homepage,
        FileUrl: null,
        source.TermsUrl);
}

/// <summary>An update held for review: what was downloaded, and why it's held. Confirming or rejecting it takes an
/// administrator (<c>tools/lists.ps1 confirm|reject -Import</c>).</summary>
public sealed record HeldUpdateResponse(int ImportId, int RecordCount, DateTimeOffset DownloadedAt, string? Reason);

/// <param name="Result"><c>Active</c> (the list now searched), <c>Replaced</c> (an earlier list), <c>HeldForReview</c>,
/// <c>Rejected</c>, <c>Failed</c> or <c>Running</c>.</param>
/// <param name="Reviewed">Whether a person confirmed or rejected it (who, isn't public).</param>
public sealed record HistoryEntryResponse(int ImportId, string Result, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int RecordCount, string? Note, bool Reviewed)
{
    public static HistoryEntryResponse From(ImportHistoryEntry entry) => new(
        entry.Id,
        entry.Active ? "Active"
            : entry.Status switch
            {
                ImportStatus.Completed => "Replaced",
                ImportStatus.PendingReview => "HeldForReview",
                _ => entry.Status.ToString(),
            },
        entry.StartedAt,
        entry.CompletedAt,
        entry.RecordCount,
        entry.Message,
        entry.ReviewedAt is not null);
}
