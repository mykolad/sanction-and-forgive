using System.Text.Json;
using System.Text.Json.Nodes;
using SanctionAndForgive.Conformance.Feeds;
using SanctionAndForgive.Conformance.Golden;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// Every answer compared with the reference implementation's, field for field (tests/conformance/golden). Run first,
// against the base state: the fictional lists as imported, nothing switched off, no allow-list entry.
[Collection("1 Golden")]
public sealed class GoldenTests
{
    private static object Request(GoldenInput input) =>
        new { reference = input.Reference, receiverName = input.ReceiverName, companyName = input.CompanyName, address = input.Address };

    private static string Key(JsonNode? node) =>
        node?["reference"]?.GetValue<string>() ?? node?["type"]?.GetValue<string>() ?? node?["code"]?.GetValue<string>() ?? "?";

    [ConformanceFact]
    public async Task TheStream_AnswersEveryGoldenInput_AsTheReferenceDoes()
    {
        await Suite.ReadyAsync();

        var lines = await Suite.StreamAsync(GoldenInputs.All.Select(Request));

        Assert.Equal(GoldenInputs.All.Count + 2, lines.Count);
        GoldenFiles.Match("stream.ndjson", [.. lines.Select(line => GoldenFiles.Normalize(Suite.Node(line)))], Key);
    }

    [ConformanceFact]
    public async Task SingleScreenings_AnswerAsTheReferenceDoes_SummaryAndDetailsIncluded()
    {
        await Suite.ReadyAsync();
        var results = new List<JsonNode?>();

        // Every tenth input: each kind of request, with the summary, the rows' order and every candidate's details.
        foreach (var input in GoldenInputs.All.Where((_, index) => index % 10 == 0))
        {
            results.Add(GoldenFiles.Normalize(Suite.Node(await Suite.ScreenAsync(Request(input)))));
        }

        GoldenFiles.Match("screenings.ndjson", results, Key);
    }

    [ConformanceFact]
    public async Task TheListsStatusAndExamples_AreAsTheReferenceHasThem()
    {
        await Suite.ReadyAsync();

        GoldenFiles.MatchDocument("sources.json", GoldenFiles.Normalize(Suite.Node(await Suite.GetAsync("api/v1/sources"))));
        GoldenFiles.MatchDocument("status.json", GoldenFiles.Normalize(Suite.Node(await Suite.GetAsync("api/v1/status"))));
        GoldenFiles.MatchDocument("examples.json", GoldenFiles.Normalize(Suite.Node(await Suite.GetAsync("api/v1/examples"))));
    }

    [ConformanceFact]
    public async Task BrowsingEveryList_PagesAndFilters_AsTheReferenceDoes()
    {
        await Suite.ReadyAsync();
        var pages = new List<JsonNode?>();
        var codes = FeedFiles.Feeds.Select(feed => feed.Code).Append(FeedFiles.ManualCode);
        var requests = new object[]
        {
            new { offset = 0, limit = 25 },
            new { offset = 25, limit = 25 },
            new { query = "volga", offset = 0, limit = 50 },
            new { query = "Иван", offset = 0, limit = 50 },
            new { type = "Individual", offset = 0, limit = 10 },
        };
        foreach (var code in codes)
        {
            foreach (var request in requests)
            {
                using var response = await Suite.PostAsync($"api/v1/lists/{code}/browse", request);
                var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
                var page = GoldenFiles.Normalize(Suite.Node(body))!.AsObject();
                page["request"] = JsonSerializer.SerializeToNode(request, Suite.Json);
                page["code"] = code;
                page["status"] = (int)response.StatusCode;
                pages.Add(page);
            }
        }

        GoldenFiles.Match("browse.ndjson", pages, node => $"{node?["code"]} {node?["request"]?.ToJsonString()}");
    }
}
