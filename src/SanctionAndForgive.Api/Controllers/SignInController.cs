using Microsoft.AspNetCore.Mvc;
using SanctionAndForgive.Api.Admin;

namespace SanctionAndForgive.Api.Controllers;

/// <summary>The admin sign-in's pages (<see cref="AdminSignIn"/>): start, the issuer's answer, and sign-out. 404 with
/// sign-in off. Not part of the API description.</summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class SignInController(AdminSignIn signIn, TimeProvider clock) : ControllerBase
{
    /// <summary>Sends the browser to sign in, remembering this sign-in's state and nonce in a cookie only the answer's
    /// page gets. SameSite=None: the answer is a POST from the issuer's site.</summary>
    [HttpGet(AdminSignIn.StartPath)]
    public async Task<IActionResult> Start([FromQuery(Name = "post_login_redirect_uri")] string? returnTo, CancellationToken cancellationToken)
    {
        if (!signIn.Enabled)
        {
            return NotFound();
        }

        var start = await signIn.StartAsync(AdminSignIn.LocalReturn(returnTo), $"{Request.Scheme}://{Request.Host}{AdminSignIn.CallbackPath}", cancellationToken);
        if (start is null)
        {
            return Text(StatusCodes.Status503ServiceUnavailable, "The sign-in service can't be reached right now. Try again in a minute.");
        }

        Response.Cookies.Append(AdminSignIn.PendingCookie, start.Cookie, PendingOptions(TimeSpan.FromMinutes(10)));
        return Redirect(start.Url);
    }

    /// <summary>The issuer's answer: the ID token becomes the session cookie (SameSite=Lax: sent when someone opens
    /// the site, never with another site's form).</summary>
    [HttpPost(AdminSignIn.CallbackPath)]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Callback([FromForm(Name = "id_token")] string? idToken, [FromForm(Name = "state")] string? state, [FromForm(Name = "error")] string? error, CancellationToken cancellationToken)
    {
        if (!signIn.Enabled)
        {
            return NotFound();
        }

        Response.Cookies.Delete(AdminSignIn.PendingCookie, PendingOptions(TimeSpan.Zero));
        if (error is not null)
        {
            // Entra refused: most often an account without the role (the registration requires an assignment).
            return Text(StatusCodes.Status403Forbidden, "The sign-in was refused. Only lists administrators can sign in here.");
        }

        Request.Cookies.TryGetValue(AdminSignIn.PendingCookie, out var pending);
        if (await signIn.CompleteAsync(pending, state, idToken, cancellationToken) is not { } done)
        {
            return Text(StatusCodes.Status400BadRequest, "The sign-in couldn't be completed. Start again from the admin page.");
        }

        Response.Cookies.Append(AdminSignIn.SessionCookie, idToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = done.Session.Expires - clock.GetUtcNow() > TimeSpan.Zero ? done.Session.Expires - clock.GetUtcNow() : TimeSpan.FromMinutes(1),
        });
        return LocalRedirect(done.ReturnTo);
    }

    /// <summary>Ends the session here (the Entra sign-in itself stays, as it did with the platform's).</summary>
    [HttpGet(AdminSignIn.SignOutPath)]
    public IActionResult SignOutHere([FromQuery(Name = "post_logout_redirect_uri")] string? returnTo)
    {
        Response.Cookies.Delete(AdminSignIn.SessionCookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return LocalRedirect(AdminSignIn.LocalReturn(returnTo));
    }

    private static CookieOptions PendingOptions(TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        Path = AdminSignIn.StartPath,
        MaxAge = maxAge,
    };

    private static ContentResult Text(int status, string text) => new() { StatusCode = status, Content = text + "\n", ContentType = "text/plain; charset=utf-8" };
}
