using System.Text;
using System.Text.Json;

namespace SanctionAndForgive.Api.Admin;

/// <summary>Who is signed in, from Container Apps' built-in authentication (Microsoft Entra ID). The platform puts
/// the signed-in user in the <c>X-MS-CLIENT-PRINCIPAL</c> header (base64 JSON with the token's claims) and doesn't
/// let clients set it, so the header can be trusted, but only where that authentication is on. Hence
/// <c>Admin:SignInEnabled</c>: the app deployment sets it in the same template that turns the authentication on
/// (infra/app.bicep). Without it, nobody is signed in, whatever a request says: the admin features fail closed.</summary>
public sealed class AdminAccess(IConfiguration configuration)
{
    /// <summary>The app role (on the app registration, infra/admin-sign-in.ps1) that may review list updates.</summary>
    public const string Role = "Lists.Admin";

    public const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";

    /// <summary>Every admin POST must carry this header with this value. A form or image on another site can't set a
    /// custom header (and the API allows no cross-origin calls), so a signed-in admin's cookie can't be used
    /// against them from elsewhere.</summary>
    public const string RequestHeader = "X-Requested-By";

    public const string RequestHeaderValue = "sanction-and-forgive-admin";

    private static readonly string[] RoleClaimTypes = ["roles", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];

    private static readonly string[] NameClaimTypes = ["preferred_username", "name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"];

    public bool Enabled => configuration.GetValue("Admin:SignInEnabled", false);

    /// <summary>The signed-in user, or null: sign-in off, no principal, or one that can't be read.</summary>
    public AdminUser? Current(HttpRequest request)
    {
        if (!Enabled || !request.Headers.TryGetValue(PrincipalHeader, out var values) || values.Count != 1 || values[0] is not { Length: > 0 and < 16_384 } encoded)
        {
            return null;
        }

        try
        {
            using var principal = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = principal.RootElement;
            var roleType = root.TryGetProperty("role_typ", out var role) ? role.GetString() : null;
            var nameType = root.TryGetProperty("name_typ", out var nameTyp) ? nameTyp.GetString() : null;
            var claims = root.GetProperty("claims").EnumerateArray()
                .Select(claim => (Type: claim.GetProperty("typ").GetString() ?? string.Empty, Value: claim.GetProperty("val").GetString() ?? string.Empty))
                .ToList();
            var roles = claims.Where(claim => claim.Type == roleType || RoleClaimTypes.Contains(claim.Type, StringComparer.Ordinal)).Select(claim => claim.Value);
            var name = NameClaimTypes.Prepend(nameType).Select(type => claims.FirstOrDefault(claim => claim.Type == type).Value).FirstOrDefault(value => !string.IsNullOrEmpty(value));
            return new AdminUser(name ?? "signed-in user", roles.Contains(Role, StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether an admin POST carries <see cref="RequestHeader"/>.</summary>
    public static bool FromThisSite(HttpRequest request) =>
        request.Headers.TryGetValue(RequestHeader, out var value) && value == RequestHeaderValue;
}

/// <param name="Name">The sign-in name (an email, usually): recorded as the reviewer.</param>
/// <param name="IsAdmin">Holds the <see cref="AdminAccess.Role"/> app role.</param>
public sealed record AdminUser(string Name, bool IsAdmin);
