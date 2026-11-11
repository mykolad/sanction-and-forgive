using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// The admin sign-in (docs/api.md, "Admin"): to the issuer and back with an ID token, the token as the session, sign-out,
// and what's never a session. Against the stand-in issuer (Harness/Issuer.cs). Nothing here changes state.
[Collection("2 Behaviour")]
public sealed class SignInTests
{
    private static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal)) : null;

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1].Replace('+', ' ') : string.Empty), StringComparer.Ordinal);

    /// <summary>Starts a sign-in: the issuer's address it sends the browser to, and the cookie that goes back with the
    /// answer.</summary>
    private static async Task<(Dictionary<string, string> Query, string Pending)> StartAsync(string returnTo)
    {
        Issuer.EnsureStarted();
        using var start = await Suite.NoRedirects.GetAsync(new Uri($".auth/login/aad?post_login_redirect_uri={Uri.EscapeDataString(returnTo)}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Found, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith(Issuer.AuthorizationEndpoint + "?", location, StringComparison.Ordinal);
        var pending = SetCookie(start, "saf-sign-in");
        Assert.NotNull(pending);
        foreach (var attribute in new[] { "httponly", "secure", "samesite=none" })
        {
            Assert.Contains(attribute, pending, StringComparison.OrdinalIgnoreCase);
        }

        return (Query(location), pending.Split(';')[0]);
    }

    private static async Task<HttpResponseMessage> AnswerAsync(string? pendingCookie, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(".auth/login/aad/callback", UriKind.Relative)) { Content = new FormUrlEncodedContent(form) };
        if (pendingCookie is not null)
        {
            request.Headers.Add("Cookie", pendingCookie);
        }

        return await Suite.NoRedirects.SendAsync(request);
    }

    private static async Task<JsonElement> MeAsync(string? cookie, string? principalHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("api/v1/admin/me", UriKind.Relative));
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (principalHeader is not null)
        {
            request.Headers.Add("X-MS-CLIENT-PRINCIPAL", principalHeader);
        }

        using var response = await Suite.Http.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string AnswerToken(Dictionary<string, string> query, string? nonce) =>
        Issuer.Token(Suite.AdminName, admin: true, nonce ?? query["nonce"], DateTimeOffset.UtcNow.AddHours(1), Issuer.ClientId, Issuer.Authority, Issuer.SigningKey);

    [ConformanceFact]
    public async Task SignIn_GoesToTheIssuer_AndItsIdTokenBecomesTheSession()
    {
        await Suite.ReadyAsync();
        var (query, pending) = await StartAsync("/allow-list");
        Assert.Equal((Issuer.ClientId, "id_token", "form_post", "openid profile"), (query["client_id"], query["response_type"], query["response_mode"], query["scope"]));
        Assert.EndsWith("/.auth/login/aad/callback", query["redirect_uri"], StringComparison.Ordinal);
        Assert.NotEqual(query["state"], query["nonce"]);

        using var answer = await AnswerAsync(pending, new() { ["id_token"] = AnswerToken(query, nonce: null), ["state"] = query["state"] });

        Assert.Equal(HttpStatusCode.Found, answer.StatusCode);
        Assert.Equal("/allow-list", answer.Headers.Location!.ToString());
        var session = SetCookie(answer, "saf-admin");
        Assert.NotNull(session);
        foreach (var attribute in new[] { "httponly", "secure", "samesite=lax", "path=/" })
        {
            Assert.Contains(attribute, session, StringComparison.OrdinalIgnoreCase);
        }

        var me = await MeAsync(session.Split(';')[0], principalHeader: null);
        Assert.Equal((true, Suite.AdminName, true), (me.GetProperty("signedIn").GetBoolean(), me.GetProperty("name").GetString(), me.GetProperty("isAdmin").GetBoolean()));
    }

    [ConformanceFact]
    public async Task AnAnswerToAnotherSignIn_OrWithoutItsCookie_IsRefused()
    {
        await Suite.ReadyAsync();
        var (query, pending) = await StartAsync("/admin");

        using var otherState = await AnswerAsync(pending, new() { ["id_token"] = AnswerToken(query, nonce: null), ["state"] = "another state" });
        using var otherNonce = await AnswerAsync(pending, new() { ["id_token"] = AnswerToken(query, "another nonce"), ["state"] = query["state"] });
        using var noCookie = await AnswerAsync(pendingCookie: null, new() { ["id_token"] = AnswerToken(query, nonce: null), ["state"] = query["state"] });
        using var refused = await AnswerAsync(pending, new() { ["error"] = "access_denied", ["state"] = query["state"] });

        Assert.Equal(
            (HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden),
            (otherState.StatusCode, otherNonce.StatusCode, noCookie.StatusCode, refused.StatusCode));
        foreach (var response in new[] { otherState, otherNonce, noCookie, refused })
        {
            Assert.Null(SetCookie(response, "saf-admin"));
        }
    }

    [ConformanceFact]
    public async Task SignInAndSignOut_ReturnOnlyToThisSite()
    {
        await Suite.ReadyAsync();
        foreach (var elsewhere in new[] { "//elsewhere.example/admin", "https://elsewhere.example/", "/\\elsewhere.example", "/\t/elsewhere.example" })
        {
            var (query, pending) = await StartAsync(elsewhere);
            using var answer = await AnswerAsync(pending, new() { ["id_token"] = AnswerToken(query, nonce: null), ["state"] = query["state"] });
            using var signOut = await Suite.NoRedirects.GetAsync(new Uri($".auth/logout?post_logout_redirect_uri={Uri.EscapeDataString(elsewhere)}", UriKind.Relative));

            Assert.Equal("/admin", answer.Headers.Location!.ToString());
            Assert.Equal((HttpStatusCode.Found, "/admin"), (signOut.StatusCode, signOut.Headers.Location!.ToString()));
            Assert.StartsWith("saf-admin=;", SetCookie(signOut, "saf-admin"), StringComparison.Ordinal);
        }
    }

    [ConformanceFact]
    public async Task ATokenTheIssuerDidntSignForThisApp_OrExpired_IsNoSession()
    {
        await Suite.ReadyAsync();
        using var otherKey = RSA.Create(2048);
        var hour = DateTimeOffset.UtcNow.AddHours(1);
        var notSessions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["signed with another key"] = Issuer.Token(Suite.AdminName, admin: true, nonce: null, hour, Issuer.ClientId, Issuer.Authority, otherKey),
            ["for another app"] = Issuer.Token(Suite.AdminName, admin: true, nonce: null, hour, "another-app", Issuer.Authority, Issuer.SigningKey),
            ["from another issuer"] = Issuer.Token(Suite.AdminName, admin: true, nonce: null, hour, Issuer.ClientId, "https://elsewhere.example/v2.0", Issuer.SigningKey),
            ["expired"] = Issuer.Token(Suite.AdminName, admin: true, nonce: null, DateTimeOffset.UtcNow.AddMinutes(-10), Issuer.ClientId, Issuer.Authority, Issuer.SigningKey),
            ["not a token"] = "not.a.token",
        };

        foreach (var (what, token) in notSessions)
        {
            var me = await MeAsync($"saf-admin={token}", principalHeader: null);
            Assert.False(me.GetProperty("signedIn").GetBoolean(), what);
        }
    }

    [ConformanceFact]
    public async Task ThePlatformsPrincipalHeader_IsNeverTrusted()
    {
        await Suite.ReadyAsync();
        var principal = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            auth_typ = "aad",
            claims = new[] { new { typ = "preferred_username", val = "forged@example.invalid" }, new { typ = "roles", val = "Lists.Admin" } },
        })));

        var me = await MeAsync(cookie: null, principal);

        Assert.False(me.GetProperty("signedIn").GetBoolean());
    }
}
