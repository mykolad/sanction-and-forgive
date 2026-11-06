using Microsoft.AspNetCore.Mvc;
using SanctionAndForgive.Api.Lists;
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
        var searched = loaded.Select(list => SourceResponse.From(list, ListSources.Find(list.Code), now));
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
            source is null ? null : list.Url,
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
