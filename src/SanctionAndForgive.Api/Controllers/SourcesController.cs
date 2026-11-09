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
        var heldUpdates = catalog.Held;
        var searched = loaded.Select(list =>
        {
            var entries = history.GetValueOrDefault(list.Code) ?? [];
            return SourceResponse.From(list, ListSources.Find(list.Code), now) with
            {
                Held = heldUpdates.GetValueOrDefault(list.Code) is { } held
                    ? new HeldUpdateResponse(held.Id, held.RecordCount, held.StartedAt, held.Message, held.Added, held.Removed, held.Changed)
                    : null,
                History = [.. entries.Take(ListLoader.HistoryShown).Select(HistoryEntryResponse.From)],
                HistoryCount = entries.Count,
            };
        });
        var notImported = ListSources.All
            .Where(source => !loaded.Any(list => string.Equals(list.Code, source.Code, StringComparison.OrdinalIgnoreCase)))
            .Select(SourceResponse.NotImported);
        return Ok(new SourcesResponse(now, [.. searched, .. notImported]));
    }

    /// <summary><c>GET /api/v1/sources/{code}/history</c>: every update of a list the API loads, newest first, from
    /// memory (never SQL). 404 for a list it doesn't load.</summary>
    [HttpGet("{code}/history")]
    [ProducesResponseType<SourceHistoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<WarmingUpResponse>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult History(string code)
    {
        if (!catalog.IsReady)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new WarmingUpResponse("warmingUp"));
        }

        if (catalog.Lists.FirstOrDefault(list => string.Equals(list.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } list)
        {
            return NotFound();
        }

        var entries = catalog.History.GetValueOrDefault(list.Code) ?? [];
        return Ok(new SourceHistoryResponse(list.Code, list.Name, [.. entries.Select(HistoryEntryResponse.From)]));
    }
}

public sealed record SourceHistoryResponse(string Code, string Name, IReadOnlyList<HistoryEntryResponse> History);

public sealed record SourcesResponse(DateTimeOffset Now, IReadOnlyList<SourceResponse> Lists);

/// <param name="State"><c>Current</c> (searched), <c>Overdue</c> (searched as last downloaded, though not updated
/// within its allowed age: results warn), <c>NotChecked</c> (no index: its cells say Not checked), <c>SwitchedOff</c>
/// (by an administrator: not searched, no cells; <c>reason</c> says when and why), or <c>NotImported</c>.</param>
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

    /// <summary>The latest updates, newest first (at most <see cref="ListLoader.HistoryShown"/>): changes, failures,
    /// held and rejected updates. Every one of them: <c>GET /api/v1/sources/{code}/history</c>.</summary>
    public IReadOnlyList<HistoryEntryResponse> History { get; init; } = [];

    /// <summary>How many updates the list has in all.</summary>
    public int HistoryCount { get; init; }

    public static SourceResponse From(LoadedList list, ListSource? source, DateTimeOffset now)
    {
        var state = list.State(now);
        string? reason = state == "Current" ? null
            : list.SwitchedOff is { } off ? $"Switched off by an administrator on {off.At.ToUniversalTime().ToString("d MMM yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)}: {off.Reason} It isn't searched until it's switched on again, and it's still updated daily."
            : list.ActiveImportId is null ? "No completed import yet."
            : list.Index is null ? "Loading, or its last load failed. Its cells say Not checked until it loads."
            : $"Couldn't be updated within {list.MaxAgeHours} hours. It's still searched as last downloaded, and every result warns about it until a check succeeds.";
        return new(
            list.Code,
            list.Name,
            state,
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

/// <summary>An update held for review: what was downloaded, what it changes against the list it was compared with (the
/// list in use when it was downloaded, which the list searched now normally is), and
/// why it's held. Confirming or rejecting it takes an administrator (<c>/admin</c>, or
/// <c>tools/lists.ps1 confirm|reject -Import</c>).</summary>
public sealed record HeldUpdateResponse(int ImportId, int RecordCount, DateTimeOffset DownloadedAt, string? Reason, int? Added, int? Removed, int? Changed);

/// <param name="Result"><c>Active</c> (the list in use: searched while it's current), <c>Replaced</c> (an earlier
/// list), <c>HeldForReview</c>, <c>Rejected</c>, <c>Failed</c> or <c>Running</c>.</param>
/// <param name="Note">Why it was held or rejected. For a failure, only that it failed: its details (an exception, a
/// path on the operator's machine) stay in <c>tools/lists.ps1 history</c>.</param>
/// <param name="Reviewed">Whether a person confirmed or rejected it (who, isn't public).</param>
/// <param name="Added">Entries new against the list it was compared with (the list in use when it was downloaded; for
/// a list's first update, all of them). With <paramref name="Removed"/> and <paramref name="Changed"/> (entries kept
/// with any field, name or address different): null where not recorded (an update from before these counts whose
/// records are gone, a failure, or a count that failed).</param>
public sealed record HistoryEntryResponse(int ImportId, string Result, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int RecordCount, string? Note, bool Reviewed, int? Added, int? Removed, int? Changed)
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
        entry.Status == ImportStatus.Failed ? "The download or the parse failed." : entry.Message,
        entry.ReviewedAt is not null,
        entry.Added,
        entry.Removed,
        entry.Changed);
}
