using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using SanctionAndForgive.Api.Admin;

namespace SanctionAndForgive.Tests.Api;

// The admin sign-in in the app (AdminSignIn): to the issuer and back, the ID token as the session, sign-out, and
// what's never a session (the platform's old principal header, a token the issuer didn't sign for this app).
public sealed class SignInTests : IDisposable
{
    private readonly ApiFactory parent = new();

    public void Dispose() => parent.Dispose();

    private HttpClient Client(bool signInEnabled) => parent.WithWebHostBuilder(builder =>
    {
        if (signInEnabled)
        {
            builder.UseSetting("Admin:ClientId", TestSignIn.ClientId).UseSetting("Admin:Authority", TestSignIn.Authority);
        }

        builder.ConfigureTestServices(services => services.AddSingleton(TestSignIn.Metadata()));
    }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static async Task<JsonElement> MeAsync(HttpClient client, string? cookie, string? header)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (header is not null)
        {
            request.Headers.Add("X-MS-CLIENT-PRINCIPAL", header);
        }

        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal)) : null;

    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient client, string? pendingCookie, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminSignIn.CallbackPath) { Content = new FormUrlEncodedContent(form) };
        if (pendingCookie is not null)
        {
            request.Headers.Add("Cookie", pendingCookie);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Starts a sign-in: the issuer's address it sends the browser to, and the cookie that goes back with
    /// the answer.</summary>
    private static async Task<(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query, string Cookie)> StartAsync(HttpClient client, string returnTo)
    {
        using var start = await client.GetAsync($"{AdminSignIn.StartPath}?post_login_redirect_uri={Uri.EscapeDataString(returnTo)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith(TestSignIn.AuthorizationEndpoint + "?", location, StringComparison.Ordinal);
        var pending = SetCookie(start, AdminSignIn.PendingCookie)!;
        Assert.Contains("httponly", pending, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", pending, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=none", pending, StringComparison.OrdinalIgnoreCase);
        return (QueryHelpers.ParseQuery(new Uri(location).Query), pending.Split(';')[0]);
    }

    [Fact]
    public async Task SignIn_GoesToTheIssuer_AndItsIdTokenBecomesTheSession()
    {
        using var client = Client(signInEnabled: true);

        var (query, pending) = await StartAsync(client, "/allow-list");
        Assert.Equal(TestSignIn.ClientId, query["client_id"]);
        Assert.Equal(("id_token", "form_post"), (query["response_type"].ToString(), query["response_mode"].ToString()));
        Assert.EndsWith(AdminSignIn.CallbackPath, query["redirect_uri"].ToString(), StringComparison.Ordinal);
        var token = TestSignIn.Token("owner@example.invalid", [AdminAccess.Role], query["nonce"], ApiFactory.Now.AddHours(1), TestSignIn.ClientId, TestSignIn.Authority, TestSignIn.Key);

        using var answer = await CallbackAsync(client, pending, new() { ["id_token"] = token, ["state"] = query["state"]! });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/allow-list", answer.Headers.Location!.ToString());
        var session = SetCookie(answer, AdminSignIn.SessionCookie)!;
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        var me = await MeAsync(client, session.Split(';')[0], header: null);
        Assert.Equal((true, "owner@example.invalid", true), (me.GetProperty("signedIn").GetBoolean(), me.GetProperty("name").GetString(), me.GetProperty("isAdmin").GetBoolean()));
    }

    [Theory]
    [InlineData("another state", false)]
    [InlineData(null, true)]
    public async Task AnAnswerToAnotherSignIn_IsRefused(string? state, bool otherNonce)
    {
        using var client = Client(signInEnabled: true);
        var (query, pending) = await StartAsync(client, "/admin");
        var token = TestSignIn.Token("owner@example.invalid", [AdminAccess.Role], otherNonce ? "another nonce" : query["nonce"], ApiFactory.Now.AddHours(1), TestSignIn.ClientId, TestSignIn.Authority, TestSignIn.Key);

        using var answer = await CallbackAsync(client, pending, new() { ["id_token"] = token, ["state"] = state ?? query["state"]! });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Null(SetCookie(answer, AdminSignIn.SessionCookie));
    }

    [Fact]
    public async Task AnAnswerWithoutTheSignInsCookie_OrARefusal_IsNotASession()
    {
        using var client = Client(signInEnabled: true);
        var (query, pending) = await StartAsync(client, "/admin");
        var token = TestSignIn.Token("owner@example.invalid", [AdminAccess.Role], query["nonce"], ApiFactory.Now.AddHours(1), TestSignIn.ClientId, TestSignIn.Authority, TestSignIn.Key);

        using var withoutCookie = await CallbackAsync(client, pendingCookie: null, new() { ["id_token"] = token, ["state"] = query["state"]! });
        using var refused = await CallbackAsync(client, pending, new() { ["error"] = "access_denied", ["state"] = query["state"]! });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.Forbidden), (withoutCookie.StatusCode, refused.StatusCode));
        Assert.Null(SetCookie(withoutCookie, AdminSignIn.SessionCookie));
        Assert.Null(SetCookie(refused, AdminSignIn.SessionCookie));
    }

    [Theory]
    [InlineData("//elsewhere.example/admin")]
    [InlineData("https://elsewhere.example/")]
    [InlineData("/\\elsewhere.example")]
    [InlineData("/\t/elsewhere.example")]
    public async Task SignInAndSignOut_ReturnOnlyToThisSite(string returnTo)
    {
        using var client = Client(signInEnabled: true);
        var (query, pending) = await StartAsync(client, returnTo);
        var token = TestSignIn.Token("owner@example.invalid", [AdminAccess.Role], query["nonce"], ApiFactory.Now.AddHours(1), TestSignIn.ClientId, TestSignIn.Authority, TestSignIn.Key);

        using var answer = await CallbackAsync(client, pending, new() { ["id_token"] = token, ["state"] = query["state"]! });
        using var signOut = await client.GetAsync($"{AdminSignIn.SignOutPath}?post_logout_redirect_uri={Uri.EscapeDataString(returnTo)}");

        Assert.Equal("/admin", answer.Headers.Location!.ToString());
        Assert.Equal((HttpStatusCode.Redirect, "/admin"), (signOut.StatusCode, signOut.Headers.Location!.ToString()));
        Assert.StartsWith(AdminSignIn.SessionCookie + "=;", SetCookie(signOut, AdminSignIn.SessionCookie), StringComparison.Ordinal);
    }

    public static TheoryData<string> NotSessions => new()
    {
        TestSignIn.Token("forged@example.invalid", [AdminAccess.Role], null, ApiFactory.Now.AddHours(1), TestSignIn.ClientId, TestSignIn.Authority, TestSignIn.OtherKey),
        TestSignIn.Token("other@example.invalid", [AdminAccess.Role], null, ApiFactory.Now.AddHours(1), "another-app", TestSignIn.Authority, TestSignIn.Key),
        TestSignIn.Token("other@example.invalid", [AdminAccess.Role], null, ApiFactory.Now.AddHours(1), TestSignIn.ClientId, "https://login.test/another-tenant/v2.0", TestSignIn.Key),
        TestSignIn.Token("late@example.invalid", [AdminAccess.Role], null, ApiFactory.Now.AddMinutes(-10), TestSignIn.ClientId, TestSignIn.Authority, TestSignIn.Key),
        "not.a.token",
    };

    [Theory]
    [MemberData(nameof(NotSessions))]
    public async Task ATokenTheIssuerDidntSignForThisApp_OrExpired_IsNoSession(string token)
    {
        using var client = Client(signInEnabled: true);

        var me = await MeAsync(client, $"{AdminSignIn.SessionCookie}={token}", header: null);

        Assert.False(me.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task TheSessionIsChecked_HoweverTheAdminPathIsCased()
    {
        using var client = Client(signInEnabled: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/API/V1/Admin/me");
        request.Headers.Add("Cookie", $"{AdminSignIn.SessionCookie}={TestSignIn.Token("owner@example.invalid", AdminAccess.Role)}");

        using var response = await client.SendAsync(request);

        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task ThePlatformsPrincipalHeader_IsNeverTrusted()
    {
        using var client = Client(signInEnabled: true);
        var principal = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            auth_typ = "aad",
            claims = new[] { new { typ = "preferred_username", val = "forged@example.invalid" }, new { typ = "roles", val = AdminAccess.Role } },
        })));

        var me = await MeAsync(client, cookie: null, principal);

        Assert.False(me.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task WithSignInOff_ItsPagesAre404()
    {
        using var client = Client(signInEnabled: false);

        using var start = await client.GetAsync(AdminSignIn.StartPath);
        using var answer = await CallbackAsync(client, pendingCookie: null, new() { ["id_token"] = "x", ["state"] = "y" });

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (start.StatusCode, answer.StatusCode));
    }
}
