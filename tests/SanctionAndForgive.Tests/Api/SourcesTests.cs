using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SanctionAndForgive.Api.Lists;
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
    public async Task LoadedLists_ShowCountFreshnessAndPage_OverdueOnesSayWhy()
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
        Assert.Equal("Overdue", uk.GetProperty("state").GetString());
        Assert.Contains("within 72 hours", uk.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("still searched", uk.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwitchedOffList_IsStillShown_WithWhenAndWhy()
    {
        factory.Load(Feed("eu", "EU", Now.AddHours(-2), Subject("Romashka LLC", SubjectKind.Entity)) with
        {
            SwitchedOff = new ListSwitchedOff(DateTimeOffset.Parse("2026-11-06T09:15:00Z", System.Globalization.CultureInfo.InvariantCulture), "The file is known to be wrong."),
        });

        var eu = (await SourcesAsync())["eu"];

        Assert.Equal(("SwitchedOff", 1), (eu.GetProperty("state").GetString(), eu.GetProperty("recordCount").GetInt32()));
        Assert.StartsWith("Switched off by an administrator on 6 Nov 2026 09:15 UTC: The file is known to be wrong.", eu.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeldUpdate_AndTheHistory_AreShown_WithoutTheReviewersName_OrFailureDetails()
    {
        factory.Load(Feed("eu", "EU", Now.AddHours(-2), Subject("Romashka LLC", SubjectKind.Entity)));
        var held = new SanctionAndForgive.Data.ImportHistoryEntry(3, "eu", SanctionAndForgive.Data.ImportStatus.PendingReview, Now.AddHours(-1), Now.AddHours(-1), 4900, "Records dropped from 6269 to 4900, more than 20 %: held for review", Active: false, ReviewedAt: null, ReviewedBy: null, Added: 3, Removed: 1372, Changed: 40);
        factory.Catalog.ReplaceHistory(new Dictionary<string, IReadOnlyList<SanctionAndForgive.Data.ImportHistoryEntry>>
        {
            ["eu"] =
            [
                new(4, "eu", SanctionAndForgive.Data.ImportStatus.Failed, Now.AddMinutes(-30), Now.AddMinutes(-30), 0, "FileNotFoundException: Could not find file '/home/owner/eu.xml'.", Active: false, ReviewedAt: null, ReviewedBy: null, Added: null, Removed: null, Changed: null),
                held,
                new(2, "eu", SanctionAndForgive.Data.ImportStatus.Rejected, Now.AddDays(-1), Now.AddDays(-1), 100, "…; rejected on review", Active: false, Now.AddDays(-1), "owner@example.invalid", Added: null, Removed: null, Changed: null),
                new(1, "eu", SanctionAndForgive.Data.ImportStatus.Completed, Now.AddDays(-2), Now.AddDays(-2), 6269, null, Active: true, ReviewedAt: null, ReviewedBy: null, Added: 6269, Removed: 0, Changed: 0),
            ],
        });
        factory.Catalog.ReplaceHeld(new Dictionary<string, SanctionAndForgive.Data.ImportHistoryEntry> { ["eu"] = held });

        var eu = (await SourcesAsync())["eu"];

        Assert.Equal((3, 4900), (eu.GetProperty("held").GetProperty("importId").GetInt32(), eu.GetProperty("held").GetProperty("recordCount").GetInt32()));
        Assert.Equal((3, 1372, 40), (eu.GetProperty("held").GetProperty("added").GetInt32(), eu.GetProperty("held").GetProperty("removed").GetInt32(), eu.GetProperty("held").GetProperty("changed").GetInt32()));
        Assert.Equal((6269, 0, 0), (eu.GetProperty("history")[3].GetProperty("added").GetInt32(), eu.GetProperty("history")[3].GetProperty("removed").GetInt32(), eu.GetProperty("history")[3].GetProperty("changed").GetInt32()));
        Assert.Equal(JsonValueKind.Null, eu.GetProperty("history")[0].GetProperty("added").ValueKind);
        Assert.Equal(4, eu.GetProperty("historyCount").GetInt32());
        Assert.Equal(["Failed", "HeldForReview", "Rejected", "Active"], eu.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("result").GetString()));
        Assert.True(eu.GetProperty("history")[2].GetProperty("reviewed").GetBoolean());
        Assert.Equal("The download or the parse failed.", eu.GetProperty("history")[0].GetProperty("note").GetString());
        Assert.DoesNotContain("owner@example.invalid", eu.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("eu.xml", eu.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("Current", eu.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AHeldUpdate_IsShown_EvenWhenLaterChecksPushedItOutOfTheHistory()
    {
        factory.Load(Feed("eu", "EU", Now.AddHours(-2), Subject("Romashka LLC", SubjectKind.Entity)));
        factory.Catalog.ReplaceHeld(new Dictionary<string, SanctionAndForgive.Data.ImportHistoryEntry>
        {
            ["eu"] = new(3, "eu", SanctionAndForgive.Data.ImportStatus.PendingReview, Now.AddDays(-3), Now.AddDays(-3), 4900, "held for review", Active: false, ReviewedAt: null, ReviewedBy: null, Added: null, Removed: null, Changed: null),
        });

        var eu = (await SourcesAsync())["eu"];

        Assert.Equal(3, eu.GetProperty("held").GetProperty("importId").GetInt32());
        Assert.Equal(0, eu.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task TheFullHistory_IsServedFromMemory_WhileTheStatusShowsTheLatest()
    {
        factory.Load(Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)));
        factory.Catalog.ReplaceHistory(new Dictionary<string, IReadOnlyList<SanctionAndForgive.Data.ImportHistoryEntry>>
        {
            ["eu"] = [.. Enumerable.Range(1, 15).Reverse().Select(id => new SanctionAndForgive.Data.ImportHistoryEntry(
                id, "eu", SanctionAndForgive.Data.ImportStatus.Completed, Now.AddDays(-id), Now.AddDays(-id), 6000 + id, null, Active: id == 15, ReviewedAt: null, ReviewedBy: null, Added: 1, Removed: 0, Changed: id))],
        });

        var eu = (await SourcesAsync())["eu"];
        using var full = await client.GetAsync(new Uri("/api/v1/sources/eu/history", UriKind.Relative));
        var body = await full.Content.ReadFromJsonAsync<JsonElement>();
        using var unknown = await client.GetAsync(new Uri("/api/v1/sources/nope/history", UriKind.Relative));

        Assert.Equal((10, 15), (eu.GetProperty("history").GetArrayLength(), eu.GetProperty("historyCount").GetInt32()));
        Assert.Equal(15, body.GetProperty("history").GetArrayLength());
        Assert.Equal((15, 1), (body.GetProperty("history")[0].GetProperty("importId").GetInt32(), body.GetProperty("history")[14].GetProperty("importId").GetInt32()));
        Assert.Equal(("eu", "EU"), (body.GetProperty("code").GetString(), body.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(0, factory.Database.Opened);
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
