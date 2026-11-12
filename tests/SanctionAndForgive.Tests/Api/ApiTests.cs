using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Api.Screening;
using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Api.ApiFactory;

namespace SanctionAndForgive.Tests.Api;

// The whole app over HTTP (section 7 and the Phase 3 done-when list).
public sealed class ApiTests : IDisposable
{
    private readonly ApiFactory factory = new();
    private readonly HttpClient client;

    public ApiTests()
    {
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private void LoadStandardLists(bool ukStale)
    {
        factory.Load(
            Feed("eu", "EU", Now.AddHours(-2), Subject("Romashka LLC", SubjectKind.Entity), Subject("Northwind Shipping LLC", SubjectKind.Entity)),
            Feed("ofac-sdn", "OFAC SDN", Now.AddHours(-3), Subject("AB Incorporated", SubjectKind.Entity), Subject("Northwind Shipping LLC", SubjectKind.Entity)),
            Feed("uk", "UK", ukStale ? Now.AddHours(-80) : Now.AddHours(-1), Subject("Volga Trade", SubjectKind.Entity)));
    }

    /// <summary>Screens <paramref name="body"/>, adding a reference unless it has one.</summary>
    private async Task<JsonElement> ScreenAsync(object body)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
        json.TryAdd("reference", "test-1");
        using var response = await client.PostAsJsonAsync("/api/v1/screenings", json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Cell(JsonElement result, string list, int entry = 0) =>
        result.GetProperty("entries")[entry].GetProperty("cells").EnumerateArray().Single(cell => cell.GetProperty("list").GetString() == list);

    [Fact]
    public async Task BeforeTheFirstLoad_ScreeningIs503WarmingUp_LivenessIsUp()
    {
        using var screening = await client.PostAsJsonAsync("/api/v1/screenings", new { reference = "test-1", companyName = "Romashka" });
        using var ready = await client.GetAsync("/readyz");
        using var health = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, screening.StatusCode);
        Assert.Equal("warmingUp", (await screening.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ListedOnEuOnly_UkOverdue_IsStillSearched_AndTheSummaryWarns()
    {
        LoadStandardLists(ukStale: true);

        var result = await ScreenAsync(new { companyName = "ТОВ Ромашка" });

        Assert.Equal("Match", result.GetProperty("outcome").GetString());
        Assert.Equal("Match", Cell(result, "eu").GetProperty("outcome").GetString());
        Assert.Equal("NoMatch", Cell(result, "ofac-sdn").GetProperty("outcome").GetString());
        var uk = Cell(result, "uk");
        Assert.Equal(("Checked", "NoMatch"), (uk.GetProperty("state").GetString(), uk.GetProperty("outcome").GetString()));
        var row = result.GetProperty("lists").EnumerateArray().Single(list => list.GetProperty("code").GetString() == "uk");
        Assert.Equal("Overdue", row.GetProperty("state").GetString());
        Assert.EndsWith(
            "Warning: couldn't be updated within 72 hours, so searched as last downloaded: UK, as of 3 Nov 2026 04:00 UTC.",
            result.GetProperty("summary").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("eu", result.GetProperty("lists")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASwitchedOffList_IsNotSearched_HasNoCells_AndTheSummarySaysWhy()
    {
        factory.Load(
            Feed("eu", "EU", Now.AddHours(-2), Subject("Romashka LLC", SubjectKind.Entity)),
            Feed("uk", "UK", Now.AddHours(-1), Subject("Volga Trade", SubjectKind.Entity)) with { SwitchedOff = new ListSwitchedOff(Now.AddHours(-5), "The file is known to be wrong.") });

        var result = await ScreenAsync(new { reference = "TX-OFF-1", companyName = "Volga Trade" });

        // Listed on the switched-off list only: not searched there, so no hit, and no Not checked cell to make it Incomplete.
        Assert.Equal("NoMatch", result.GetProperty("outcome").GetString());
        Assert.Equal(["eu"], result.GetProperty("entries")[0].GetProperty("cells").EnumerateArray().Select(cell => cell.GetProperty("list").GetString()));
        var row = result.GetProperty("lists").EnumerateArray().Single(list => list.GetProperty("code").GetString() == "uk");
        Assert.Equal("SwitchedOff", row.GetProperty("state").GetString());
        Assert.Equal(
            "No match found in: EU, as of 6 Nov 2026 10:00 UTC. Not searched: UK (switched off by an administrator: The file is known to be wrong).",
            result.GetProperty("summary").GetString());
        var audit = Assert.Single(factory.Logs.GetSnapshot(), record => record.Message.Contains("TX-OFF-1", StringComparison.Ordinal));
        Assert.Matches(@"uk@\S+ off", audit.Message);
    }

    [Fact]
    public async Task AHitOnAnOverdueList_CountsAsUsual()
    {
        LoadStandardLists(ukStale: true);

        var result = await ScreenAsync(new { companyName = "Volga Trade" });

        Assert.Equal("Match", result.GetProperty("outcome").GetString());
        Assert.Equal("Match", Cell(result, "uk").GetProperty("outcome").GetString());
        Assert.Contains("Possible match on UK.", result.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Contains("Warning: couldn't be updated within 72 hours, so searched as last downloaded: UK,", result.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnlistedName_AllCurrent_NoMatchWithEveryListAndDate_OverdueList_NoMatchWithAWarning()
    {
        LoadStandardLists(ukStale: false);
        var current = await ScreenAsync(new { receiverName = "Zephyrine Quillbrook" });
        LoadStandardLists(ukStale: true);
        var stale = await ScreenAsync(new { receiverName = "Zephyrine Quillbrook" });

        Assert.Equal("NoMatch", current.GetProperty("outcome").GetString());
        var summary = current.GetProperty("summary").GetString()!;
        Assert.StartsWith("No match found in: EU, as of 6 Nov 2026 10:00 UTC; OFAC SDN, as of", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("not sanctioned", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Warning", summary, StringComparison.Ordinal);
        Assert.Equal("NoMatch", stale.GetProperty("outcome").GetString());
        Assert.EndsWith("UK, as of 3 Nov 2026 04:00 UTC. Warning: couldn't be updated within 72 hours, so searched as last downloaded: UK, as of 3 Nov 2026 04:00 UTC.", stale.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWithoutAnIndex_IsNotChecked()
    {
        factory.Load(Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)) with { Index = null });

        var result = await ScreenAsync(new { companyName = "Romashka" });

        Assert.Equal("Incomplete", result.GetProperty("outcome").GetString());
        Assert.Equal("NotChecked", Cell(result, "eu").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, Cell(result, "eu").GetProperty("percent").ValueKind);
        Assert.Contains("Not checked: EU.", result.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiverCompanyAndAddress_OneColumnEach_BestCandidateExplained()
    {
        LoadStandardLists(ukStale: false);

        var result = await ScreenAsync(new { receiverName = "Ivan Ivanov", companyName = "AB Corp", address = "Tallinn" });

        Assert.Equal(["Receiver", "Company", "Address"], result.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()));
        var ofac = Cell(result, "ofac-sdn", entry: 1);
        Assert.Equal("Review", ofac.GetProperty("outcome").GetString());
        var best = ofac.GetProperty("bestCandidate");
        Assert.Equal("AB Incorporated", best.GetProperty("listedName").GetString());
        Assert.Equal("https://example.invalid/record", best.GetProperty("sourceUrl").GetString());
        Assert.NotEmpty(best.GetProperty("alignment").EnumerateArray());
        Assert.Contains("not a legal determination", result.GetProperty("disclaimer").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(result.GetProperty("matcherVersion").GetString()));
    }

    [Fact]
    public async Task Response_CarriesEachListsVersion_AndTheListedAddresses()
    {
        var subject = new ListedSubject("E1", SubjectKind.Entity, "Romashka LLC", [new ListedName("Romashka LLC", false)], [new ListedAddress("Lenina 5, Moscow", "RU")], "RUS", null);
        factory.Load(Feed("eu", "EU", Now, subject) with { ActiveImportId = 42 });

        var result = await ScreenAsync(new { companyName = "Romashka" });

        Assert.Equal(42, result.GetProperty("lists")[0].GetProperty("version").GetInt32());
        Assert.Equal(["Lenina 5, Moscow (RU)"], Cell(result, "eu").GetProperty("bestCandidate").GetProperty("listedAddresses").EnumerateArray().Select(address => address.GetString()));
    }

    [Fact]
    public async Task Screening_IsLoggedByReference_WithEachListsResult_AndNeverTheInput()
    {
        LoadStandardLists(ukStale: true);

        var result = await ScreenAsync(new { reference = "TX-2026-0001", receiverName = "Шевченко Т.Г.", companyName = "ТОВ Ромашка", address = "вул. Хрещатик 1, Київ" });

        Assert.Equal("TX-2026-0001", result.GetProperty("reference").GetString());
        var records = factory.Logs.GetSnapshot();
        var audit = Assert.Single(records, record => record.Message.Contains("TX-2026-0001", StringComparison.Ordinal));
        Assert.Contains("Match", audit.Message, StringComparison.Ordinal);
        Assert.Contains("eu Match", audit.Message, StringComparison.Ordinal);
        Assert.Contains("uk NoMatch", audit.Message, StringComparison.Ordinal);
        Assert.Matches(@"uk@\S+ overdue", audit.Message);
        foreach (var entered in new[] { "Шевченко", "Ромашка", "Хрещатик", "Київ" })
        {
            Assert.DoesNotContain(records, record => record.Message.Contains(entered, StringComparison.Ordinal) || (record.Exception?.ToString().Contains(entered, StringComparison.Ordinal) ?? false));
        }
    }

    [Fact]
    public async Task ManualSource_AppearsAsARow_WithNoCodeChange()
    {
        var manual = new SanctionAndForgive.Api.Lists.LoadedList("ee-mfa", "Estonia MFA national list", null, null, 7, Now.AddDays(-30), Now.AddDays(-30), 1,
            ListIndex.Build([Subject("Northwind Shipping OÜ", SubjectKind.Entity)]));
        factory.Load(Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)), manual);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/status");
        var result = await ScreenAsync(new { companyName = "Northwind Shipping OU" });

        var row = status.GetProperty("lists").EnumerateArray().Single(list => list.GetProperty("code").GetString() == "ee-mfa");
        Assert.Equal("Current", row.GetProperty("state").GetString());
        Assert.Equal("Match", Cell(result, "ee-mfa").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Screening_StatusAndExamples_NeverTouchTheDatabase()
    {
        LoadStandardLists(ukStale: false);

        await ScreenAsync(new { receiverName = "Шевченко Т.Г.", companyName = "ТОВ Ромашка", address = "Москва" });
        await client.GetFromJsonAsync<JsonElement>("/api/v1/status");
        await client.GetFromJsonAsync<JsonElement>("/api/v1/examples");
        await client.GetFromJsonAsync<JsonElement>("/api/v1/sources");

        Assert.Equal(0, factory.Database.Opened);
    }

    [Theory]
    [InlineData("""{"reference":"test-1"}""")]
    [InlineData("""{"reference":"test-1","receiverName":"  ","companyName":"","address":null}""")]
    [InlineData("""{"companyName":"Romashka"}""")]
    [InlineData("""{"reference":"Ivan Ivanov","companyName":"Romashka"}""")]
    [InlineData("""{"reference":"-x","companyName":"Romashka"}""")]
    [InlineData("""{"reference":"TX-1\n","companyName":"Romashka"}""")]
    public async Task NothingEntered_OrNoValidReference_Is400(string json)
    {
        LoadStandardLists(ukStale: false);

        using var response = await client.PostAsync("/api/v1/screenings", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FieldOver300Characters_Is400()
    {
        LoadStandardLists(ukStale: false);

        using var response = await client.PostAsJsonAsync("/api/v1/screenings", new { reference = "test-1", companyName = new string('a', 301) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Examples_AreEntitiesFromTheLiveLists()
    {
        factory.Load(Feed("eu", "EU", Now, Subject("Romashka Trading LLC", SubjectKind.Entity), Subject("Taras Shevchenko", SubjectKind.Individual),
            new ListedSubject("C1", SubjectKind.Entity, "Volga Shipping", [new ListedName("Volga Shipping", false), new ListedName("Волга Шиппинг", false)], [], null, null)));

        var examples = await client.GetFromJsonAsync<JsonElement>("/api/v1/examples");

        var names = examples.EnumerateArray().Select(example => example.GetProperty("companyName").GetString()).ToList();
        Assert.Contains("Волга Шиппинг", names);
        Assert.DoesNotContain("Taras Shevchenko", names);
        Assert.All(examples.EnumerateArray(), example => Assert.Equal(JsonValueKind.Null, example.GetProperty("receiverName").ValueKind));
    }

    [Fact]
    public async Task Page_Script_AndOpenApi_AreServed()
    {
        var page = await client.GetStringAsync("/");
        using var script = await client.GetAsync("/app.js");
        using var openApi = await client.GetAsync("/openapi/v1.json");

        Assert.Contains("id=\"screen-form\"", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("/api/v1/screenings", await openApi.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public async Task OverTheLimit_Is429_PerClient()
    {
        using var factory = new ApiFactory(rateLimitPerMinute: 2);
        factory.Load(Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)));
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.PostAsJsonAsync("/api/v1/screenings", new { reference = $"test-{i}", companyName = "Romashka" });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }
}

public sealed class ScreeningGateTests
{
    /// <summary>Takes one of <paramref name="gate"/>'s places until <paramref name="release"/> is set.</summary>
    private static async Task<Task<string?>> HoldAsync(ScreeningGate gate, ManualResetEventSlim release)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = Task.Run(() => gate.TryRunAsync<string>(() =>
        {
            entered.SetResult();
            release.Wait();
            return "held";
        }, CancellationToken.None));
        await entered.Task;
        return held;
    }

    [Fact]
    public async Task WhenEveryPlaceIsTaken_AScreeningIs503Busy_WithRetryAfter_AndOnceFreeItRuns()
    {
        using var factory = new ApiFactory();
        using var small = factory.WithWebHostBuilder(builder => builder.UseSetting("Screening:MaxConcurrent", "1").UseSetting("Screening:MaxWaiting", "0"));
        small.Services.GetRequiredService<ListCatalog>().Replace([Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity))]);
        using var client = small.CreateClient();
        using var release = new ManualResetEventSlim();
        var held = await HoldAsync(small.Services.GetRequiredService<ScreeningGate>(), release);

        using (var busy = await client.PostAsJsonAsync("/api/v1/screenings", new { reference = "test-1", companyName = "Romashka" }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal("busy", (await busy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
            Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter?.Delta);
        }

        release.Set();
        Assert.Equal("held", await held);
        using var after = await client.PostAsJsonAsync("/api/v1/screenings", new { reference = "test-2", companyName = "Romashka" });
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task AScreeningWaitsForAFreePlace_WhileThereIsRoomToWait()
    {
        using var gate = new ScreeningGate(1, 1, TimeSpan.FromMinutes(1));
        using var release = new ManualResetEventSlim();
        var held = await HoldAsync(gate, release);

        var waiting = gate.TryRunAsync(() => "waited", CancellationToken.None);
        var refused = await gate.TryRunAsync(() => "refused", CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        Assert.Null(refused);

        release.Set();
        Assert.Equal(("held", "waited"), (await held, await waiting));
        Assert.Equal("again", await gate.TryRunAsync(() => "again", CancellationToken.None));
    }

    [Fact]
    public async Task AScreeningThatWaitsLongerThanAllowed_IsRefused_AndLeavesNoPlaceTaken()
    {
        using var gate = new ScreeningGate(1, 10, TimeSpan.FromMilliseconds(100));
        using var release = new ManualResetEventSlim();
        var held = await HoldAsync(gate, release);

        var waited = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await gate.TryRunAsync(() => "too late", CancellationToken.None));
        Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(90), $"refused after {waited.ElapsedMilliseconds} ms, before its wait");

        release.Set();
        Assert.Equal("held", await held);
        Assert.Equal("again", await gate.TryRunAsync(() => "again", CancellationToken.None));
    }
}
