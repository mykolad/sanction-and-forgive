using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>POST /api/v1/screenings</c>. Reads memory only: a screening never touches the database. The input
/// is never stored or logged; it comes back only in this response.</summary>
[ApiController]
[Route("api/v1/screenings")]
public sealed class ScreeningsController(ListCatalog catalog, Screener screener, MatchingOptions options, TimeProvider clock) : ControllerBase
{
    public const int MaxFieldLength = 300;

    [HttpPost]
    [EnableRateLimiting(RateLimits.Screenings)]
    [ProducesResponseType<ScreeningResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<WarmingUpResponse>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Screen(ScreeningRequestBody body)
    {
        var fields = new[] { (nameof(body.ReceiverName), body.ReceiverName), (nameof(body.CompanyName), body.CompanyName), (nameof(body.Address), body.Address) };
        foreach (var (name, value) in fields.Where(field => field.Item2?.Length > MaxFieldLength))
        {
            ModelState.AddModelError(name, $"At most {MaxFieldLength} characters.");
        }

        if (fields.All(field => string.IsNullOrWhiteSpace(field.Item2)))
        {
            ModelState.AddModelError(string.Empty, "Enter a receiver, a company or an address.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!catalog.IsReady)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new WarmingUpResponse("warmingUp"));
        }

        var now = clock.GetUtcNow();
        var lists = catalog.Lists;
        var screened = lists.Select(list => new ScreenedList(list.Code, list.Name, list.LastCheckedAt, list.IsCurrent(now) ? list.Index : null)).ToList();
        var result = screener.Screen(new ScreeningRequest(body.ReceiverName, body.CompanyName, body.Address), screened);

        // Rows with hits first (best percentage across the entries), then the others in their usual order.
        var best = result.Entries.SelectMany(entry => entry.Cells).GroupBy(cell => cell.List).ToDictionary(group => group.Key, group => group.Max(cell => cell.Percent ?? -1));
        var rows = lists.Select(list => ListResponse.From(list, now))
            .OrderByDescending(row => best.GetValueOrDefault(row.Code, -1) >= options.ReviewMinPercent ? best[row.Code] : -1)
            .ToList();

        return Ok(new ScreeningResponse(
            result.Outcome.ToString(),
            Summary(result, lists, now),
            ScreeningResponse.DisclaimerText,
            result.MatcherVersion,
            rows,
            [.. result.Entries.Select(entry => EntryResponse.From(entry, options))]));
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
}
