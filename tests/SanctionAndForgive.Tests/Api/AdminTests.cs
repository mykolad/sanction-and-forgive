using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SanctionAndForgive.Api.Admin;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Tests.Api;

// /api/v1/admin and /admin: who is signed in (Container Apps' Entra sign-in, through its principal header), and
// reviewing held updates, which needs the Lists.Admin role, the site's own request header, and sign-in turned on.
public sealed class AdminTests : IDisposable
{
    private readonly ApiFactory parent = new();
    private readonly FakeReview review = new();

    public void Dispose() => parent.Dispose();

    private HttpClient Client(bool signInEnabled) => parent.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Admin:SignInEnabled", signInEnabled ? "true" : "false");
        builder.ConfigureTestServices(services => services.AddSingleton<IImportReview>(review));
    }).CreateClient();

    /// <summary>The header Container Apps sends for a signed-in Entra user: base64 JSON of the token's claims.</summary>
    private static string Principal(string name, params string[] roles) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        auth_typ = "aad",
        name_typ = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
        role_typ = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        claims = new[] { new { typ = "preferred_username", val = name }, new { typ = "name", val = "Owner" } }
            .Concat(roles.Select(role => new { typ = "roles", val = role })),
    })));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? principal, bool fromThisSite)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (principal is not null)
        {
            request.Headers.Add(AdminAccess.PrincipalHeader, principal);
        }

        if (fromThisSite)
        {
            request.Headers.Add(AdminAccess.RequestHeader, AdminAccess.RequestHeaderValue);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Me_SaysWhoIsSignedIn_AndWhetherTheyAreAnAdmin()
    {
        using var client = Client(signInEnabled: true);

        var anonymous = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/me");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me");
        request.Headers.Add(AdminAccess.PrincipalHeader, Principal("owner@example.invalid", AdminAccess.Role));
        using var response = await client.SendAsync(request);
        var admin = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((true, false), (anonymous.GetProperty("enabled").GetBoolean(), anonymous.GetProperty("signedIn").GetBoolean()));
        Assert.StartsWith("/.auth/login/aad", anonymous.GetProperty("signInUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(("owner@example.invalid", true), (admin.GetProperty("name").GetString(), admin.GetProperty("isAdmin").GetBoolean()));
    }

    [Fact]
    public async Task WithSignInOff_APrincipalHeaderIsIgnored_AndReviewingIs404()
    {
        using var client = Client(signInEnabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me");
        request.Headers.Add(AdminAccess.PrincipalHeader, Principal("forged@example.invalid", AdminAccess.Role));

        using var me = await client.SendAsync(request);
        using var confirm = await PostAsync(client, "/api/v1/admin/imports/7/confirm", Principal("forged@example.invalid", AdminAccess.Role), fromThisSite: true);

        Assert.False((await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        Assert.Empty(review.Calls);
    }

    [Theory]
    [InlineData(false, false, true, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, true, HttpStatusCode.Forbidden)]
    [InlineData(true, true, false, HttpStatusCode.BadRequest)]
    public async Task Reviewing_NeedsSignIn_TheRole_AndTheSitesHeader(bool signedIn, bool admin, bool fromThisSite, HttpStatusCode expected)
    {
        using var client = Client(signInEnabled: true);
        var principal = signedIn ? (admin ? Principal("owner@example.invalid", AdminAccess.Role) : Principal("visitor@example.invalid")) : null;

        using var response = await PostAsync(client, "/api/v1/admin/imports/7/confirm", principal, fromThisSite);

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(review.Calls);
    }

    [Theory]
    [InlineData("confirm", true, ReviewResult.Confirmed, HttpStatusCode.OK)]
    [InlineData("reject", false, ReviewResult.Rejected, HttpStatusCode.OK)]
    [InlineData("confirm", true, ReviewResult.NotPending, HttpStatusCode.Conflict)]
    [InlineData("reject", false, ReviewResult.NotFound, HttpStatusCode.NotFound)]
    public async Task AnAdmin_ConfirmsOrRejects_AsThemselves(string action, bool confirm, ReviewResult result, HttpStatusCode expected)
    {
        review.Result = result;
        using var client = Client(signInEnabled: true);

        using var response = await PostAsync(client, $"/api/v1/admin/imports/7/{action}", Principal("owner@example.invalid", AdminAccess.Role), fromThisSite: true);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal((7, confirm, "owner@example.invalid"), Assert.Single(review.Calls));
    }

    [Fact]
    public async Task TheAdminPage_IsServed_AndInTheMenu()
    {
        using var client = Client(signInEnabled: true);

        var page = await client.GetStringAsync(new Uri("/admin", UriKind.Relative));
        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("id=\"held\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin\" aria-current=\"page\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin\"", home, StringComparison.Ordinal);
    }

    private sealed class FakeReview : IImportReview
    {
        public ReviewResult Result { get; set; } = ReviewResult.Confirmed;

        public List<(int ImportId, bool Confirm, string Reviewer)> Calls { get; } = [];

        public Task<ReviewOutcome> ReviewAsync(int importId, bool confirm, string reviewer, CancellationToken cancellationToken)
        {
            Calls.Add((importId, confirm, reviewer));
            return Task.FromResult(new ReviewOutcome(Result, Reloaded: true));
        }
    }
}
