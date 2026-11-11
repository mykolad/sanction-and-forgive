using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SanctionAndForgive.Api.Admin;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Tests.Api;

// /api/v1/admin and /admin: who is signed in (the session cookie AdminSignIn accepted), and reviewing held updates,
// which needs the Lists.Admin role, the site's own request header, and sign-in turned on.
public sealed class AdminTests : IDisposable
{
    private readonly ApiFactory parent = new();
    private readonly FakeReview review = new();
    private readonly FakeAllowList allowList = new();
    private readonly FakeSwitch switches = new();

    public void Dispose() => parent.Dispose();

    private HttpClient Client(bool signInEnabled) => parent.WithWebHostBuilder(builder =>
    {
        if (signInEnabled)
        {
            builder.UseSetting("Admin:ClientId", TestSignIn.ClientId).UseSetting("Admin:Authority", TestSignIn.Authority);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<IImportReview>(review).AddSingleton<IAllowListAdmin>(allowList).AddSingleton<IListSwitch>(switches)
            .AddSingleton(TestSignIn.Metadata()));
    }).CreateClient();

    private static void SignedInAs(HttpRequestMessage request, string session) => request.Headers.Add("Cookie", $"{AdminSignIn.SessionCookie}={session}");

    /// <summary>A signed-in user's session: their ID token, as the sign-in leaves it in the session cookie.</summary>
    private static string Principal(string name, params string[] roles) => TestSignIn.Token(name, roles);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? principal, bool fromThisSite) =>
        await SendAsync(client, HttpMethod.Post, path, principal, fromThisSite, body: null);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? principal, bool fromThisSite, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (principal is not null)
        {
            SignedInAs(request, principal);
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
        SignedInAs(request, Principal("owner@example.invalid", AdminAccess.Role));
        using var response = await client.SendAsync(request);
        var admin = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((true, false), (anonymous.GetProperty("enabled").GetBoolean(), anonymous.GetProperty("signedIn").GetBoolean()));
        Assert.StartsWith("/.auth/login/aad", anonymous.GetProperty("signInUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(("owner@example.invalid", true), (admin.GetProperty("name").GetString(), admin.GetProperty("isAdmin").GetBoolean()));
    }

    [Fact]
    public async Task WithSignInOff_ASessionIsIgnored_AndReviewingIs404()
    {
        using var client = Client(signInEnabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me");
        SignedInAs(request, Principal("forged@example.invalid", AdminAccess.Role));

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

    [Theory]
    [InlineData(false, false, true, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, true, HttpStatusCode.Forbidden)]
    [InlineData(true, true, false, HttpStatusCode.BadRequest)]
    public async Task SwitchingAList_NeedsSignIn_TheRole_AndTheSitesHeader(bool signedIn, bool admin, bool fromThisSite, HttpStatusCode expected)
    {
        using var client = Client(signInEnabled: true);
        var principal = signedIn ? (admin ? Principal("owner@example.invalid", AdminAccess.Role) : Principal("visitor@example.invalid")) : null;

        using var off = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/lists/eu/switch-off", principal, fromThisSite, new { reason = "The file is known to be wrong." });
        using var on = await PostAsync(client, "/api/v1/admin/lists/eu/switch-on", principal, fromThisSite);

        Assert.Equal((expected, expected), (off.StatusCode, on.StatusCode));
        Assert.Empty(switches.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task SwitchingAListOff_NeedsAReason(string? reason)
    {
        using var client = Client(signInEnabled: true);

        using var response = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/lists/eu/switch-off", Principal("owner@example.invalid", AdminAccess.Role), fromThisSite: true, new { reason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(switches.Calls);
    }

    [Theory]
    [InlineData("switch-off", true, SwitchResult.Switched, HttpStatusCode.OK, "SwitchedOff")]
    [InlineData("switch-on", false, SwitchResult.Switched, HttpStatusCode.OK, "On")]
    [InlineData("switch-off", true, SwitchResult.Unchanged, HttpStatusCode.Conflict, "AlreadyOff")]
    [InlineData("switch-on", false, SwitchResult.Unchanged, HttpStatusCode.Conflict, "AlreadyOn")]
    [InlineData("switch-off", true, SwitchResult.NotFound, HttpStatusCode.NotFound, null)]
    public async Task AnAdmin_SwitchesAListOffOrOn_AsThemselves(string action, bool off, SwitchResult result, HttpStatusCode expected, string? answer)
    {
        switches.Result = result;
        using var client = Client(signInEnabled: true);

        using var response = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/lists/eu/{action}", Principal("owner@example.invalid", AdminAccess.Role), fromThisSite: true, new { reason = " The file is known to be wrong. " });

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(("eu", off, off ? "The file is known to be wrong." : null, "owner@example.invalid"), Assert.Single(switches.Calls));
        if (answer is not null)
        {
            Assert.Equal(answer, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
        }
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

    [Fact]
    public async Task TheAllowListPage_IsServed_AndOnEveryPagesMenu()
    {
        using var client = Client(signInEnabled: true);

        var page = await client.GetStringAsync(new Uri("/allow-list", UriKind.Relative));

        Assert.Contains("id=\"add-form\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/allow-list\" aria-current=\"page\"", page, StringComparison.Ordinal);
        foreach (var other in new[] { "/", "/lists", "/status", "/admin" })
        {
            Assert.Contains("href=\"/allow-list\"", await client.GetStringAsync(new Uri(other, UriKind.Relative)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheAllowList_IsForAdminsOnly_EvenToRead()
    {
        using var client = Client(signInEnabled: true);

        using var anonymous = await SendAsync(client, HttpMethod.Get, "/api/v1/admin/allow-list", principal: null, fromThisSite: false, body: null);
        using var visitor = await SendAsync(client, HttpMethod.Get, "/api/v1/admin/allow-list", Principal("visitor@example.invalid"), fromThisSite: false, body: null);
        using var admin = await SendAsync(client, HttpMethod.Get, "/api/v1/admin/allow-list", Principal("owner@example.invalid", AdminAccess.Role), fromThisSite: false, body: null);

        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.OK), (anonymous.StatusCode, visitor.StatusCode, admin.StatusCode));
        var entries = (await admin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entries");
        Assert.Equal("Huamei Trading", entries[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("Huamei Trading", "A customer since 2019", HttpStatusCode.OK)]
    [InlineData("", "A reason", HttpStatusCode.BadRequest)]
    [InlineData("Huamei Trading", "  ", HttpStatusCode.BadRequest)]
    [InlineData("Listed Party LLC", "A reason", HttpStatusCode.Conflict)]
    [InlineData("While Loading", "A reason", HttpStatusCode.ServiceUnavailable)]
    public async Task AddingAnEntry_NeedsANameAndAReason_AndNeverAListedParty(string name, string reason, HttpStatusCode expected)
    {
        using var client = Client(signInEnabled: true);

        using var response = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/allow-list", Principal("owner@example.invalid", AdminAccess.Role), fromThisSite: true, new { name, reason });

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal((name, reason, "owner@example.invalid"), Assert.Single(allowList.Added));
        }
    }

    [Fact]
    public async Task ANameWithPossibleMatches_IsShownThem_AndAddedOnlyOnceTheyAreConfirmed()
    {
        using var client = Client(signInEnabled: true);
        var owner = Principal("owner@example.invalid", AdminAccess.Role);

        using var first = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/allow-list", owner, fromThisSite: true, new { name = "Huamei Technologies", reason = "A customer" });
        var refusal = await first.Content.ReadFromJsonAsync<JsonElement>();
        using var otherKey = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/allow-list", owner, fromThisSite: true, new { name = "Huamei Technologies", reason = "A customer", confirmed = new[] { "eu/1" } });
        var addedBefore = allowList.Added.Count;
        using var confirmed = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/allow-list", owner, fromThisSite: true, new { name = "Huamei Technologies", reason = "A customer", confirmed = new[] { "ofac-sdn/123" } });

        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Equal("confirm", refusal.GetProperty("code").GetString());
        var hit = Assert.Single(refusal.GetProperty("possibleMatches").EnumerateArray().ToList());
        Assert.Equal(("ofac-sdn/123", "OFAC SDN", "SHENZHEN HUAMEI LIANYUN", 72, "CHINA-EO13846"),
            (hit.GetProperty("key").GetString(), hit.GetProperty("listName").GetString(), hit.GetProperty("listedName").GetString(), hit.GetProperty("percent").GetInt32(), hit.GetProperty("programme").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, otherKey.StatusCode);
        Assert.Equal(0, addedBefore);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(("Huamei Technologies", "A customer", "owner@example.invalid"), Assert.Single(allowList.Added));
    }

    [Fact]
    public async Task ConfirmedKeys_AreBounded()
    {
        using var client = Client(signInEnabled: true);
        var owner = Principal("owner@example.invalid", AdminAccess.Role);

        using var empty = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/allow-list", owner, fromThisSite: true, new { name = "Huamei Trading", reason = "x", confirmed = new[] { "" } });
        using var tooMany = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/allow-list", owner, fromThisSite: true, new { name = "Huamei Trading", reason = "x", confirmed = Enumerable.Range(0, 201).Select(i => $"eu/{i}").ToArray() });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (empty.StatusCode, tooMany.StatusCode));
        Assert.Empty(allowList.Added);
    }

    [Fact]
    public async Task ChangingTheAllowList_NeedsTheSitesHeader_AndRemovingAnUnknownEntryIs404()
    {
        using var client = Client(signInEnabled: true);
        var owner = Principal("owner@example.invalid", AdminAccess.Role);

        using var noHeader = await SendAsync(client, HttpMethod.Post, "/api/v1/admin/allow-list", owner, fromThisSite: false, new { name = "Huamei Trading", reason = "x" });
        using var removed = await PostAsync(client, "/api/v1/admin/allow-list/1/remove", owner, fromThisSite: true);
        using var unknown = await PostAsync(client, "/api/v1/admin/allow-list/99/remove", owner, fromThisSite: true);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.NotFound), (noHeader.StatusCode, removed.StatusCode, unknown.StatusCode));
        Assert.Empty(allowList.Added);
        Assert.Equal([(1, "owner@example.invalid")], allowList.Removed);
    }

    private sealed class FakeAllowList : IAllowListAdmin
    {
        public List<(string Name, string Reason, string By)> Added { get; } = [];

        public List<(int Id, string By)> Removed { get; } = [];

        public Task<IReadOnlyList<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AllowListRecord>>([new AllowListRecord(1, "Huamei Trading", "A customer", "owner@example.invalid", ApiFactory.Now, null, null)]);

        public Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, IReadOnlySet<string> confirmed, CancellationToken cancellationToken)
        {
            if (name == "Listed Party LLC")
            {
                return Task.FromResult(AllowListAddResult.Refused(AllowListAddOutcome.Listed) with { ListedAs = "EU: Listed Party LLC" });
            }

            if (name == "While Loading")
            {
                return Task.FromResult(AllowListAddResult.Refused(AllowListAddOutcome.Unavailable));
            }

            List<AllowListPossibleMatch> possible = name == "Huamei Technologies"
                ? [new AllowListPossibleMatch("ofac-sdn/123", "ofac-sdn", "OFAC SDN", "SHENZHEN HUAMEI LIANYUN", "SHENZHEN HUAMEI LIANYUN", 72, "CHINA-EO13846")]
                : [];
            if (possible.Any(hit => !confirmed.Contains(hit.Key)))
            {
                return Task.FromResult(AllowListAddResult.Refused(AllowListAddOutcome.Confirm) with { PossibleMatches = possible, PossibleMatchCount = possible.Count });
            }

            Added.Add((name, reason, addedBy));
            return Task.FromResult(new AllowListAddResult(AllowListAddOutcome.Added, Added.Count, null, null, possible, possible.Count, Reloaded: true));
        }

        public Task<AllowListRemoveResult> RemoveAsync(int id, string removedBy, CancellationToken cancellationToken)
        {
            if (id != 1)
            {
                return Task.FromResult(new AllowListRemoveResult(Removed: false, Reloaded: false));
            }

            Removed.Add((id, removedBy));
            return Task.FromResult(new AllowListRemoveResult(Removed: true, Reloaded: true));
        }
    }

    private sealed class FakeSwitch : IListSwitch
    {
        public SwitchResult Result { get; set; } = SwitchResult.Switched;

        public List<(string Code, bool Off, string? Reason, string By)> Calls { get; } = [];

        public Task<SwitchOutcome> SwitchAsync(string code, bool off, string? reason, string by, CancellationToken cancellationToken)
        {
            Calls.Add((code, off, reason, by));
            return Task.FromResult(new SwitchOutcome(Result, Reloaded: true));
        }
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
