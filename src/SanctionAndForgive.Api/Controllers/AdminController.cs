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

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var result = await allowList.AddAsync(body.Name!.Trim(), body.Reason!.Trim(), user!.Name, cancellationToken);
        if (result.Unavailable)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new AllowListRefusal("A list can't be checked right now (loading, or not current), so the name can't be checked against it. Try again later."));
        }

        if (result.Id is not { } id)
        {
            return Conflict(new AllowListRefusal($"This name is a match on {result.ListedAs}: a listed party can't be allow-listed."));
        }

        AllowListChanged(logger, id, "added", user.Name);
        return Ok(new AllowListChangeResponse(id, result.Reloaded));
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
}

/// <param name="Enabled">Whether sign-in is set up on this deployment at all.</param>
/// <param name="IsAdmin">Holds the Lists.Admin app role: may confirm or reject held updates.</param>
public sealed record AdminMeResponse(bool Enabled, bool SignedIn, string? Name, bool IsAdmin, string? SignInUrl, string? SignOutUrl);

/// <param name="Result"><c>Confirmed</c>, <c>Rejected</c>, or <c>NotPending</c> (409: it isn't held any more).</param>
/// <param name="Reloaded">Whether the lists were reloaded at once; if not, the review stands and the next refresh
/// (within 15 minutes) shows it.</param>
public sealed record ReviewResponse(int ImportId, string Result, bool Reloaded);

public sealed record AllowListAddRequest(string? Name, string? Reason);

/// <param name="Reloaded">Whether the allow-list in use was reloaded at once; if not, the change is stored and the next
/// refresh (within 15 minutes) applies it (a removal applies at once regardless).</param>
public sealed record AllowListChangeResponse(int Id, bool Reloaded);

public sealed record AllowListRefusal(string Message);

public sealed record AllowListResponse(IReadOnlyList<AllowListEntryResponse> Entries);

/// <param name="RemovedAt">Null while the entry is in force.</param>
public sealed record AllowListEntryResponse(int Id, string Name, string Reason, string AddedBy, DateTimeOffset AddedAt, string? RemovedBy, DateTimeOffset? RemovedAt)
{
    public static AllowListEntryResponse From(AllowListRecord record) =>
        new(record.Id, record.Name, record.Reason, record.AddedBy, record.AddedAt, record.RemovedBy, record.RemovedAt);
}
