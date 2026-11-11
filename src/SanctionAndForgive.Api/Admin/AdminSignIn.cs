using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SanctionAndForgive.Api.Admin;

/// <summary>Where the admin sign-in is: the app registration (<c>Admin:ClientId</c>, from infra/admin-sign-in.ps1) and
/// its tenant (<c>Admin:TenantId</c>), or any OpenID Connect issuer (<c>Admin:Authority</c>: the conformance suite's
/// stand-in). Sign-in is on only with both.</summary>
public sealed record SignInSettings(string? ClientId, string? Authority)
{
    public bool Enabled => ClientId is { Length: > 0 } && Authority is { Length: > 0 };

    public string MetadataAddress => (Authority ?? "https://sign-in-off.invalid").TrimEnd('/') + "/.well-known/openid-configuration";

    public static SignInSettings From(IConfiguration configuration)
    {
        var authority = configuration["Admin:Authority"] is { Length: > 0 } configured ? configured
            : configuration["Admin:TenantId"] is { Length: > 0 } tenant ? $"https://login.microsoftonline.com/{tenant}/v2.0"
            : null;
        return new SignInSettings(configuration["Admin:ClientId"], authority);
    }

    /// <summary>The issuer's published endpoints and signing keys, fetched when first needed and cached (a day; again
    /// sooner when a token names a key it doesn't know).</summary>
    public IConfigurationManager<OpenIdConnectConfiguration> Metadata() =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = MetadataAddress.StartsWith("https://", StringComparison.Ordinal) });
}

/// <summary>A signed-in administrator, from a valid ID token, and when the token (so the session) ends.</summary>
public sealed record SignedIn(AdminUser User, DateTimeOffset Expires);

/// <summary>A sign-in on its way to the issuer: where to send the browser, and the cookie that remembers it.</summary>
public sealed record SignInStart(string Url, string Cookie);

/// <summary>The admin sign-in, in the app (owner's decision, 2026-11-11: no platform sidecar). OpenID Connect with
/// Microsoft Entra ID: the browser is sent to sign in and comes back with an ID token posted to
/// <see cref="CallbackPath"/> (the same paths as Container Apps' built-in sign-in had, so the app registration is
/// unchanged). No client secret: the ID token is signed by Entra and carries the app roles. The token itself is the
/// session, an HttpOnly cookie checked on every admin request (signature, issuer, audience, expiry), so nothing is kept
/// on the server and any replica can check it. A session lasts as long as the token, about an hour.</summary>
public sealed partial class AdminSignIn(SignInSettings settings, IConfigurationManager<OpenIdConnectConfiguration> metadata, TimeProvider clock, ILogger<AdminSignIn> logger)
{
    public const string SessionCookie = "saf-admin";

    public const string PendingCookie = "saf-sign-in";

    public const string StartPath = "/.auth/login/aad";

    public const string CallbackPath = "/.auth/login/aad/callback";

    public const string SignOutPath = "/.auth/logout";

    /// <summary>Where a sign-in or sign-out returns to when it isn't told, or is told somewhere not on this site.</summary>
    public const string DefaultReturn = "/admin";

    // Clocks differ a little between the issuer and a replica.
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    // The sign-in name: the account (an email, usually), else the display name.
    private static readonly string[] NameClaims = ["preferred_username", "name"];

    // A cookie holds at most about 4 KB.
    private const int MaxToken = 4000;

    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false };

    public bool Enabled => settings.Enabled;

    /// <summary>Only a path on this site: never another site's address (<c>//host</c>, <c>/\host</c>).</summary>
    public static string LocalReturn(string? path) =>
        path is { Length: > 0 and <= 512 } && path[0] == '/' && (path.Length == 1 || (path[1] != '/' && path[1] != '\\')) ? path : DefaultReturn;

    /// <summary>The issuer's sign-in address for this sign-in, with a new state and nonce, and the cookie that keeps
    /// them; null if the issuer can't be reached.</summary>
    public async Task<SignInStart?> StartAsync(string returnTo, string callbackUri, CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationAsync(cancellationToken);
        if (configuration?.AuthorizationEndpoint is not { Length: > 0 } endpoint)
        {
            return null;
        }

        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["client_id"] = settings.ClientId,
            ["response_type"] = "id_token",
            ["response_mode"] = "form_post",
            ["redirect_uri"] = callbackUri,
            ["scope"] = "openid profile",
            ["state"] = state,
            ["nonce"] = nonce,
        });
        return new SignInStart(url, $"{state}.{nonce}.{Base64UrlEncoder.Encode(LocalReturn(returnTo))}");
    }

    /// <summary>The ID token posted back, checked against the sign-in it answers (the cookie from
    /// <see cref="StartAsync"/>): the session and where to go, or null with the reason logged.</summary>
    public async Task<(SignedIn Session, string ReturnTo)?> CompleteAsync(string? pending, string? state, string? idToken, CancellationToken cancellationToken)
    {
        var parts = pending?.Split('.');
        if (parts is not { Length: 3 } || state is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[0]), Encoding.ASCII.GetBytes(state)))
        {
            Refused(logger, "the state doesn't match a sign-in started here");
            return null;
        }

        if (idToken is null || await ValidateAsync(idToken, parts[1], cancellationToken) is not { } session)
        {
            Refused(logger, "the ID token isn't valid");
            return null;
        }

        string returnTo;
        try
        {
            returnTo = LocalReturn(Base64UrlEncoder.Decode(parts[2]));
        }
        catch (FormatException)
        {
            returnTo = DefaultReturn;
        }

        SignedInAs(logger, session.User.Name);
        return (session, returnTo);
    }

    /// <summary>The administrator an ID token names, if it is one this issuer signed for this app and it hasn't
    /// expired; with <paramref name="nonce"/>, also only if it answers that sign-in.</summary>
    public async Task<SignedIn?> ValidateAsync(string token, string? nonce, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || token.Length is 0 or > MaxToken || await ConfigurationAsync(cancellationToken) is not { } configuration)
        {
            return null;
        }

        var result = await handler.ValidateTokenAsync(token, Parameters(configuration));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Entra rotates its keys: look again once (the manager allows that at most every five minutes).
            metadata.RequestRefresh();
            if (await ConfigurationAsync(cancellationToken) is not { } refreshed)
            {
                return null;
            }

            result = await handler.ValidateTokenAsync(token, Parameters(refreshed));
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var expires = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        if (jwt.ValidTo == DateTime.MinValue || expires + Skew < now || (jwt.ValidFrom != DateTime.MinValue && new DateTimeOffset(jwt.ValidFrom, TimeSpan.Zero) - Skew > now))
        {
            return null;
        }

        if (nonce is not null && !(jwt.TryGetPayloadValue<string>("nonce", out var said) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(said), Encoding.UTF8.GetBytes(nonce))))
        {
            return null;
        }

        var name = NameClaims
            .Select(type => jwt.TryGetPayloadValue<string>(type, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        var isAdmin = jwt.Claims.Any(claim => claim.Type == "roles" && claim.Value == AdminAccess.Role);
        return new SignedIn(new AdminUser(name ?? "signed-in user", isAdmin), expires);
    }

    private TokenValidationParameters Parameters(OpenIdConnectConfiguration configuration) => new()
    {
        ValidIssuer = configuration.Issuer,
        ValidAudience = settings.ClientId,
        IssuerSigningKeys = configuration.SigningKeys,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        RequireSignedTokens = true,

        // Checked above against the app's clock instead.
        ValidateLifetime = false,
    };

    private async Task<OpenIdConnectConfiguration?> ConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await metadata.GetConfigurationAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            Unreachable(logger, exception.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin sign-in refused: {Reason}")]
    private static partial void Refused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin sign-in: {Name}")]
    private static partial void SignedInAs(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "The sign-in issuer's metadata couldn't be read ({Error})")]
    private static partial void Unreachable(ILogger logger, string error);
}
