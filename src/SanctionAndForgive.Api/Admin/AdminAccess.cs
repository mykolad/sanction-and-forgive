namespace SanctionAndForgive.Api.Admin;

/// <summary>Who is signed in: the administrator whose session cookie <see cref="AdminSignIn"/> accepted for this request
/// (<see cref="UseAdminSession"/>). Nothing a request says about itself counts otherwise; with sign-in off nobody is
/// signed in, and the admin features fail closed.</summary>
public sealed class AdminAccess(AdminSignIn signIn)
{
    /// <summary>The app role (on the app registration, infra/admin-sign-in.ps1) that may review list updates.</summary>
    public const string Role = "Lists.Admin";

    /// <summary>Every admin POST must carry this header with this value. A form or image on another site can't set a
    /// custom header (and the API allows no cross-origin calls), so a signed-in admin's cookie can't be used
    /// against them from elsewhere.</summary>
    public const string RequestHeader = "X-Requested-By";

    public const string RequestHeaderValue = "sanction-and-forgive-admin";

    private const string UserItem = "sanction-and-forgive.admin";

    public bool Enabled => signIn.Enabled;

    /// <summary>The signed-in user, or null: sign-in off, no session, or one that isn't valid.</summary>
    public AdminUser? Current(HttpRequest request) =>
        request.HttpContext.Items.TryGetValue(UserItem, out var user) ? user as AdminUser : null;

    /// <summary>Checks the session cookie of every admin API request once, before the controller reads
    /// <see cref="Current"/>.</summary>
    public static void UseAdminSession(WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/v1/admin", StringComparison.Ordinal)
            && context.Request.Cookies.TryGetValue(AdminSignIn.SessionCookie, out var token) && token is { Length: > 0 }
            && await context.RequestServices.GetRequiredService<AdminSignIn>().ValidateAsync(token, nonce: null, context.RequestAborted) is { } session)
        {
            context.Items[UserItem] = session.User;
        }

        await next(context);
    });

    /// <summary>Whether an admin POST carries <see cref="RequestHeader"/>.</summary>
    public static bool FromThisSite(HttpRequest request) =>
        request.Headers.TryGetValue(RequestHeader, out var value) && value == RequestHeaderValue;
}

/// <param name="Name">The sign-in name (an email, usually): recorded as the reviewer.</param>
/// <param name="IsAdmin">Holds the <see cref="AdminAccess.Role"/> app role.</param>
public sealed record AdminUser(string Name, bool IsAdmin);
