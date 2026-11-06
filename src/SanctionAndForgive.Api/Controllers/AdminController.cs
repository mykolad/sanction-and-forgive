using Microsoft.AspNetCore.Mvc;
using SanctionAndForgive.Api.Admin;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>/api/v1/admin</c>: who is signed in, reviewing list updates held for review, and the allow-list.
/// Sign-in is Container Apps' built-in Microsoft Entra ID authentication (<see cref="AdminAccess"/>); everything but
/// <c>me</c> needs the <see cref="AdminAccess.Role"/> app role, and every change the site's own request header.
/// Screening never needs sign-in.</summary>
[ApiController]
[Route("api/v1/admin")]
public sealed partial class AdminController(AdminAccess access, IImportReview review, IAllowListAdmin allowList, ILogger<AdminController> logger) : ControllerBase
{
    private const string SignInPath = "/.auth/login/aad?post_login_redirect_uri=/admin";

    private const string SignOutPath = "/.auth/logout?post_logout_redirect_uri=/admin";

    // A key is a source code (up to 32 characters), a slash and an external id (up to 200): room to spare.
    private const int MaxKey = 256;

    [HttpGet("me")]
    public AdminMeResponse Me()
    {
        var user = access.Current(Request);
        return new AdminMeResponse(access.Enabled, user is not null, user?.Name, user?.IsAdmin ?? false, access.Enabled ? SignInPath : null, user is not null ? SignOutPath : null);
    }

    /// <summary>Makes a held update the active list.</summary>
    [HttpPost("imports/{importId:int}/confirm")]
    public Task<IActionResult> Confirm(int importId, CancellationToken cancellationToken) => ReviewAsync(importId, confirm: true, cancellationToken);

    /// <summary>Discards a held update; the active list stays.</summary>
    [HttpPost("imports/{importId:int}/reject")]
    public Task<IActionResult> Reject(int importId, CancellationToken cancellationToken) => ReviewAsync(importId, confirm: false, cancellationToken);

    /// <summary>The allow-list: entries in force and removed ones, newest first. Administrators only: entries can be
    /// people's names.</summary>
    [HttpGet("allow-list")]
    public async Task<IActionResult> AllowList(CancellationToken cancellationToken)
    {
        if (Refuse(change: false, out _) is { } refusal)
        {
            return refusal;
        }

        var history = await allowList.HistoryAsync(cancellationToken);
        return Ok(new AllowListResponse([.. history.Select(AllowListEntryResponse.From)]));
    }

    /// <summary>Adds an entry: a name known to be a legitimate party, and why.</summary>
    [HttpPost("allow-list")]
    public async Task<IActionResult> AddToAllowList(AllowListAddRequest body, CancellationToken cancellationToken)
    {
        if (Refuse(change: true, out var user) is { } refusal)
        {
            return refusal;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body.Name?.Trim() is not { Length: > 0 and <= AllowListStore.MaxName })
        {
            errors[nameof(body.Name)] = [$"1 to {AllowListStore.MaxName} characters: the name exactly as it will be entered."];
        }

        if (body.Reason?.Trim() is not { Length: > 0 and <= AllowListStore.MaxReason })
        {
            errors[nameof(body.Reason)] = [$"1 to {AllowListStore.MaxReason} characters: why this party isn't the listed one."];
        }

        if (body.Confirmed is { } keys && (keys.Count > AllowListAdmin.MaxPossibleMatches || keys.Any(key => key is not { Length: > 0 and <= MaxKey })))
        {
            errors[nameof(body.Confirmed)] = [$"At most {AllowListAdmin.MaxPossibleMatches} keys of 1 to {MaxKey} characters, as the possible matches gave them."];
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var confirmed = new HashSet<string>(body.Confirmed?.OfType<string>() ?? [], StringComparer.Ordinal);
        var result = await allowList.AddAsync(body.Name!.Trim(), body.Reason!.Trim(), user!.Name, confirmed, cancellationToken);
        var count = result.PossibleMatchCount;
        switch (result.Outcome)
        {
            case AllowListAddOutcome.Unavailable:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new AllowListRefusal(AllowListRefusal.Unavailable, "A list can't be checked right now (loading, or not current), so the name can't be checked against it. Try again later.", []));
            case AllowListAddOutcome.Exists:
                return Conflict(new AllowListRefusal(AllowListRefusal.Exists, $"This name is already on the allow-list (entry {result.ExistingId}). To change its reason, remove it and add it again.", []));
            case AllowListAddOutcome.Listed:
                return Conflict(new AllowListRefusal(AllowListRefusal.Listed, $"This name is a match on {result.ListedAs}: a listed party can't be allow-listed.", []));
            case AllowListAddOutcome.TooBroad:
                return Conflict(new AllowListRefusal(AllowListRefusal.TooBroad, $"This name is a possible match for {count} listed parties, more than the {AllowListAdmin.MaxPossibleMatches} an entry can be checked against: it's too generic to allow-list. Enter the full name, legal form included.", [.. result.PossibleMatches.Select(PossibleMatchResponse.From)]));
            case AllowListAddOutcome.Confirm:
                var message = count == 1
                    ? "This name is a possible match for a listed party. Once it's on the allow-list, that match is shown as allowed and no longer raises the result. Add it only if this name is not that party."
                    : $"This name is a possible match for {count} listed parties. Once it's on the allow-list, these matches are shown as allowed and no longer raise the result. Add it only if this name is none of them.";
                return Conflict(new AllowListRefusal(AllowListRefusal.Confirm, message, [.. result.PossibleMatches.Select(PossibleMatchResponse.From)]));
            case AllowListAddOutcome.Added:
            default:
                // The possible matches confirmed, by list and listed id: what the administrator accepted, never the name.
                AllowListAdded(logger, result.Id!.Value, user.Name, count == 0 ? "none" : string.Join(", ", result.PossibleMatches.Select(hit => hit.Key)));
                return Ok(new AllowListChangeResponse(result.Id.Value, result.Reloaded));
        }
    }

    /// <summary>Removes an entry (it stays in the history).</summary>
    [HttpPost("allow-list/{id:int}/remove")]
    public async Task<IActionResult> RemoveFromAllowList(int id, CancellationToken cancellationToken)
    {
        if (Refuse(change: true, out var user) is { } refusal)
        {
            return refusal;
        }

        var result = await allowList.RemoveAsync(id, user!.Name, cancellationToken);
        if (!result.Removed)
        {
            return NotFound();
        }

        AllowListChanged(logger, id, "removed", user.Name);
        return Ok(new AllowListChangeResponse(id, result.Reloaded));
    }

    private async Task<IActionResult> ReviewAsync(int importId, bool confirm, CancellationToken cancellationToken)
    {
        if (Refuse(change: true, out var user) is { } refusal)
        {
            return refusal;
        }

        var outcome = await review.ReviewAsync(importId, confirm, user!.Name, cancellationToken);
        Reviewed(logger, importId, outcome.Result, user.Name);
        return outcome.Result switch
        {
            ReviewResult.Confirmed or ReviewResult.Rejected => Ok(new ReviewResponse(importId, outcome.Result.ToString(), outcome.Reloaded)),
            ReviewResult.NotFound => NotFound(),
            _ => Conflict(new ReviewResponse(importId, outcome.Result.ToString(), Reloaded: false)),
        };
    }

    /// <summary>Why this request may not go on (null if it may): sign-in off (404), not signed in (401), without the
    /// role (403), or a change without the site's own header (400).</summary>
    private IActionResult? Refuse(bool change, out AdminUser? user)
    {
        user = access.Current(Request);
        if (!access.Enabled)
        {
            return NotFound();
        }

        if (user is null)
        {
            return Unauthorized();
        }

        // A plain 403: the sign-in is the platform's, so the app has no authentication scheme for Forbid().
        if (!user.IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (change && !AdminAccess.FromThisSite(Request))
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { [AdminAccess.RequestHeader] = ["Send this request from the admin page."] }));
        }

        return null;
    }

    // The reviewer is an administrator's sign-in name: the audit of who changed a list. No screening input.
    [LoggerMessage(Level = LogLevel.Information, Message = "Import {ImportId} review: {Result} by {Reviewer}")]
    private static partial void Reviewed(ILogger logger, int importId, ReviewResult result, string reviewer);

    // By id only: an entry's name can be a person's.
    [LoggerMessage(Level = LogLevel.Information, Message = "Allow-list entry {Id} {Change} by {Administrator}")]
    private static partial void AllowListChanged(ILogger logger, int id, string change, string administrator);

    [LoggerMessage(Level = LogLevel.Information, Message = "Allow-list entry {Id} added by {Administrator}; possible matches confirmed: {Confirmed}")]
    private static partial void AllowListAdded(ILogger logger, int id, string administrator, string confirmed);
}

/// <param name="Enabled">Whether sign-in is set up on this deployment at all.</param>
/// <param name="IsAdmin">Holds the Lists.Admin app role: may confirm or reject held updates.</param>
public sealed record AdminMeResponse(bool Enabled, bool SignedIn, string? Name, bool IsAdmin, string? SignInUrl, string? SignOutUrl);

/// <param name="Result"><c>Confirmed</c>, <c>Rejected</c>, or <c>NotPending</c> (409: it isn't held any more).</param>
/// <param name="Reloaded">Whether the lists were reloaded at once; if not, the review stands and the next refresh
/// (within 15 minutes) shows it.</param>
public sealed record ReviewResponse(int ImportId, string Result, bool Reloaded);

/// <param name="Confirmed">The keys of the possible matches the administrator has seen and accepts (from a 409 with
/// code <c>confirm</c>); omitted the first time.</param>
public sealed record AllowListAddRequest(string? Name, string? Reason, IReadOnlyList<string?>? Confirmed);

/// <param name="Reloaded">Whether the allow-list in use was reloaded at once; if not, the change is stored and the next
/// refresh (within 15 minutes) applies it (a removal applies at once regardless).</param>
public sealed record AllowListChangeResponse(int Id, bool Reloaded);

/// <param name="Code"><c>confirm</c>: possible matches to confirm (<paramref name="PossibleMatches"/>); <c>listed</c>: the
/// name is a Match, never added; <c>exists</c>: the name is already an entry in force; <c>tooBroad</c>: a possible match
/// for more listed parties than can be confirmed (the closest of them listed); <c>unavailable</c>: a list can't be
/// checked now.</param>
public sealed record AllowListRefusal(string Code, string Message, IReadOnlyList<PossibleMatchResponse> PossibleMatches)
{
    public const string Confirm = "confirm";
    public const string Listed = "listed";
    public const string Unavailable = "unavailable";
    public const string Exists = "exists";
    public const string TooBroad = "tooBroad";
}

/// <param name="Key">What to send back in <c>confirmed</c>: <c>{list code}/{external id}</c>.</param>
public sealed record PossibleMatchResponse(string Key, string List, string ListName, string ListedName, string Alias, int Percent, string? Programme)
{
    public static PossibleMatchResponse From(AllowListPossibleMatch hit) =>
        new(hit.Key, hit.List, hit.ListName, hit.ListedName, hit.Alias, hit.Percent, hit.Programme);
}

public sealed record AllowListResponse(IReadOnlyList<AllowListEntryResponse> Entries);

/// <param name="RemovedAt">Null while the entry is in force.</param>
public sealed record AllowListEntryResponse(int Id, string Name, string Reason, string AddedBy, DateTimeOffset AddedAt, string? RemovedBy, DateTimeOffset? RemovedAt)
{
    public static AllowListEntryResponse From(AllowListRecord record) =>
        new(record.Id, record.Name, record.Reason, record.AddedBy, record.AddedAt, record.RemovedBy, record.RemovedAt);
}
