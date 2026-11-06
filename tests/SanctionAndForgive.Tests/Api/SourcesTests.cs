using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SanctionAndForgive.Lists;
using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Api.ApiFactory;

namespace SanctionAndForgive.Tests.Api;

// GET /api/v1/sources and the /status page: every list searched, then every catalogued feed that isn't, with why.
public sealed class SourcesTests : IDisposable
{
    private readonly ApiFactory factory = new();
    private readonly HttpClient client;

    public SourcesTests()
    {
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private async Task<Dictionary<string, JsonElement>> SourcesAsync()
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/sources");
        return body.GetProperty("lists").EnumerateArray().ToDictionary(list => list.GetProperty("code").GetString()!);
    }

    [Fact]
    public async Task LoadedLists_ShowCountFreshnessAndPage_StaleOnesSayWhy()
    {
        factory.Load(
            Feed("eu", "EU", Now.AddHours(-2), Subject("Romashka LLC", SubjectKind.Entity), Subject("Volga Trade", SubjectKind.Entity)),
            Feed("uk", "UK", Now.AddHours(-80), Subject("Volga Trade", SubjectKind.Entity)));

        var lists = await SourcesAsync();

        var eu = lists["eu"];
        Assert.Equal(("Current", "Active", 2), (eu.GetProperty("state").GetString(), eu.GetProperty("status").GetString(), eu.GetProperty("recordCount").GetInt32()));
        Assert.Equal(Now.AddHours(-2), eu.GetProperty("lastCheckedAt").GetDateTimeOffset());
        Assert.Equal(ListSources.Find("eu")!.Homepage, eu.GetProperty("homepage").GetString());
        Assert.Equal(JsonValueKind.Null, eu.GetProperty("reason").ValueKind);
        Assert.EndsWith("token=" + ListSources.EuPublicToken, eu.GetProperty("fileUrl").GetString(), StringComparison.Ordinal);
        var uk = lists["uk"];
        Assert.Equal("NotChecked", uk.GetProperty("state").GetString());
        Assert.Contains("72 hours", uk.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CataloguedFeeds_NotLoaded_AreListedWithTheirReason()
    {
        factory.Load(Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)));

        var lists = await SourcesAsync();

        Assert.Equal(ListSources.All.Count, lists.Count);
        foreach (var source in ListSources.All.Where(source => source.Status != FeedStatus.Active))
        {
            var row = lists[source.Code];
            Assert.Equal(("NotImported", source.Status.ToString()), (row.GetProperty("state").GetString(), row.GetProperty("status").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("reason").GetString()), $"{source.Code} needs a reason");
        }

        Assert.Contains("non-commercial", lists["un"].GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualSource_IsListed_AsManual()
    {
        factory.Load(new SanctionAndForgive.Api.Lists.LoadedList("ee-mfa", "Estonia MFA national list", null, null, 7, Now.AddDays(-30), Now.AddDays(-30), 1,
            ListIndex.Build([Subject("Northwind Shipping OÜ", SubjectKind.Entity)])));

        var row = (await SourcesAsync())["ee-mfa"];

        Assert.Equal(("Current", "Manual"), (row.GetProperty("state").GetString(), row.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task BeforeTheFirstLoad_Is503()
    {
        using var response = await client.GetAsync(new Uri("/api/v1/sources", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void EveryCataloguedFeed_HasItsPublishersPage_AndEveryInactiveOneAReason()
    {
        Assert.All(ListSources.All, source => Assert.StartsWith("https://", source.Homepage, StringComparison.Ordinal));
        Assert.All(ListSources.All.Where(source => source.Status != FeedStatus.Active), source => Assert.False(string.IsNullOrWhiteSpace(source.Note), source.Code));
    }

    [Fact]
    public async Task StatusPage_IsServed_AtSlashStatus()
    {
        var page = await client.GetStringAsync(new Uri("/status", UriKind.Relative));

        Assert.Contains("id=\"sources-table\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryPage_HasTheMenu_WithTheStatusPage_AndMarksWhereYouAre()
    {
        foreach (var (path, current) in new[] { ("/", "/"), ("/status", "/status") })
        {
            var page = await client.GetStringAsync(new Uri(path, UriKind.Relative));
            var menu = page[page.IndexOf("<nav class=\"menu\"", StringComparison.Ordinal)..page.IndexOf("</nav>", StringComparison.Ordinal)];

            Assert.Contains("href=\"/status\"", menu, StringComparison.Ordinal);
            Assert.Contains($"href=\"{current}\" aria-current=\"page\"", menu, StringComparison.Ordinal);
        }
    }
}
