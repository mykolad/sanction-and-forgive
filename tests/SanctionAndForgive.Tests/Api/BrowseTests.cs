using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Api.ApiFactory;

namespace SanctionAndForgive.Tests.Api;

// POST /api/v1/lists/{code}/browse and the /lists page: a list's entries as screening sees them, from memory.
public sealed class BrowseTests : IDisposable
{
    private readonly ApiFactory factory = new();
    private readonly HttpClient client;

    public BrowseTests()
    {
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private void Load() => factory.Load(
        Feed("eu", "EU", Now,
            new ListedSubject("EU.2", SubjectKind.Individual, "Zoltán Müller", [new ListedName("Zoltán Müller", IsWeak: false), new ListedName("Золтан Мюллер", IsWeak: true)], [new ListedAddress("Budapest", "HU")], "HU-1", "https://example.invalid/2"),
            new ListedSubject("EU.1", SubjectKind.Entity, "Acme Trading LLC", [new ListedName("Acme Trading LLC", IsWeak: false)], [], "RU-1", null),
            new ListedSubject("EU.3", SubjectKind.Entity, "Borealis Shipping", [new ListedName("Borealis Shipping", IsWeak: false)], [], "RU-1", null)));

    private async Task<JsonElement> BrowseAsync(object body)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/lists/eu/browse", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Names(JsonElement page) =>
        [.. page.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("primaryName").GetString()!)];

    [Fact]
    public async Task Entries_ComeInNameOrder_ByPage_WithEverythingTheListGives()
    {
        Load();

        var first = await BrowseAsync(new { offset = 0, limit = 2 });
        var second = await BrowseAsync(new { offset = 2, limit = 2 });

        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(["Acme Trading LLC", "Borealis Shipping"], Names(first));
        Assert.Equal(["Zoltán Müller"], Names(second));
        var muller = second.GetProperty("entries")[0];
        Assert.Equal(("EU.2", "Individual", "HU-1"), (muller.GetProperty("externalId").GetString(), muller.GetProperty("type").GetString(), muller.GetProperty("programme").GetString()));
        Assert.True(muller.GetProperty("names")[1].GetProperty("isWeak").GetBoolean());
        Assert.Equal("HU", muller.GetProperty("addresses")[0].GetProperty("countryCode").GetString());
        Assert.Equal("Current", first.GetProperty("list").GetProperty("state").GetString());
    }

    [Fact]
    public async Task LeadingQuotesAndCase_DoNotChangeTheOrder()
    {
        factory.Load(Feed("eu", "EU", Now,
            Subject("'Tango Four'", SubjectKind.Entity), Subject("«alpha» Group", SubjectKind.Entity), Subject("Bravo Shipping", SubjectKind.Entity)));

        var page = await BrowseAsync(new { offset = 0, limit = 10 });

        Assert.Equal(["«alpha» Group", "Bravo Shipping", "'Tango Four'"], Names(page));
    }

    [Theory]
    [InlineData("muller", "Zoltán Müller")]
    [InlineData("ZOLTAN  mül", "Zoltán Müller")]
    [InlineData("мюллер", "Zoltán Müller")]
    [InlineData("eu.3", "Borealis Shipping")]
    public async Task TheFilter_FindsAnyName_IgnoringCaseAndDiacritics_OrTheExactId(string query, string expected)
    {
        Load();

        var page = await BrowseAsync(new { query, offset = 0, limit = 50 });

        Assert.Equal([expected], Names(page));
    }

    [Fact]
    public async Task TheTypeFilter_KeepsOneKind()
    {
        Load();

        var page = await BrowseAsync(new { type = "entity", offset = 0, limit = 50 });

        Assert.Equal(["Acme Trading LLC", "Borealis Shipping"], Names(page));
    }

    [Theory]
    [InlineData("""{"offset":0,"limit":0}""")]
    [InlineData("""{"offset":0,"limit":101}""")]
    [InlineData("""{"offset":-1,"limit":10}""")]
    [InlineData("""{"type":"vessel","offset":0,"limit":10}""")]
    public async Task ABadRequest_Is400(string json)
    {
        Load();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/api/v1/lists/eu/browse", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownList_Is404_AndBeforeTheFirstLoad_503()
    {
        using (var warming = await client.PostAsJsonAsync("/api/v1/lists/eu/browse", new { offset = 0, limit = 10 }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, warming.StatusCode);
        }

        Load();
        using var unknown = await client.PostAsJsonAsync("/api/v1/lists/xx/browse", new { offset = 0, limit = 10 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Browsing_NeverTouchesTheDatabase_AndTheFilterIsNeverLogged()
    {
        Load();

        await BrowseAsync(new { query = "Zephyrine Quillbrook", offset = 0, limit = 10 });

        Assert.Equal(0, factory.Database.Opened);
        Assert.DoesNotContain(factory.Logs.GetSnapshot(), record => record.Message.Contains("Zephyrine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheListsPage_IsServed_AndInTheMenu()
    {
        var page = await client.GetStringAsync(new Uri("/lists", UriKind.Relative));
        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("id=\"entries\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/lists\" aria-current=\"page\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/lists\"", home, StringComparison.Ordinal);
    }
}
