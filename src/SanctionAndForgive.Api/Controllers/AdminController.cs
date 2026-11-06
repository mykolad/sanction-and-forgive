using Microsoft.AspNetCore.Mvc;
using SanctionAndForgive.Api.Admin;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>/api/v1/admin</c>: who is signed in, and reviewing list updates held for review. Sign-in is Container
/// Apps' built-in Microsoft Entra ID authentication (<see cref="AdminAccess"/>); reviewing needs the
/// <see cref="AdminAccess.Role"/> app role. Screening never needs sign-in.</summary>
[ApiController]
[Route("api/v1/admin")]
public sealed partial class AdminController(AdminAccess access, IImportReview review, ILogger<AdminController> logger) : ControllerBase
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

    private async Task<IActionResult> ReviewAsync(int importId, bool confirm, CancellationToken cancellationToken)
    {
        if (!access.Enabled)
        {
            return NotFound();
        }

        if (access.Current(Request) is not { } user)
        {
            return Unauthorized();
        }

        // A plain 403: the sign-in is the platform's, so the app has no authentication scheme for Forbid().
        if (!user.IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!AdminAccess.FromThisSite(Request))
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { [AdminAccess.RequestHeader] = ["Send this request from the admin page."] }));
        }

        var outcome = await review.ReviewAsync(importId, confirm, user.Name, cancellationToken);
        Reviewed(logger, importId, outcome.Result, user.Name);
        return outcome.Result switch
        {
            ReviewResult.Confirmed or ReviewResult.Rejected => Ok(new ReviewResponse(importId, outcome.Result.ToString(), outcome.Reloaded)),
            ReviewResult.NotFound => NotFound(),
            _ => Conflict(new ReviewResponse(importId, outcome.Result.ToString(), Reloaded: false)),
        };
    }

    // The reviewer is an administrator's sign-in name: the audit of who changed a list. No screening input.
    [LoggerMessage(Level = LogLevel.Information, Message = "Import {ImportId} review: {Result} by {Reviewer}")]
    private static partial void Reviewed(ILogger logger, int importId, ReviewResult result, string reviewer);
}

/// <param name="Enabled">Whether sign-in is set up on this deployment at all.</param>
/// <param name="IsAdmin">Holds the Lists.Admin app role: may confirm or reject held updates.</param>
public sealed record AdminMeResponse(bool Enabled, bool SignedIn, string? Name, bool IsAdmin, string? SignInUrl, string? SignOutUrl);

/// <param name="Result"><c>Confirmed</c>, <c>Rejected</c>, or <c>NotPending</c> (409: it isn't held any more).</param>
/// <param name="Reloaded">Whether the lists were reloaded at once; if not, the review stands and the next refresh
/// (within 15 minutes) shows it.</param>
public sealed record ReviewResponse(int ImportId, string Result, bool Reloaded);
