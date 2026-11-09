using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SanctionAndForgive.Conformance.Feeds;
using SanctionAndForgive.Conformance.Golden;
using SanctionAndForgive.Conformance.Harness;
using Xunit.Abstractions;

namespace SanctionAndForgive.Conformance.Tests;

/// <summary>Only with <c>CONFORMANCE_REFERENCE_URL</c>: the reference implementation, running against the same
/// database as the one under test.</summary>
public sealed class ReferenceFactAttribute : FactAttribute
{
    public ReferenceFactAttribute()
    {
        if (Settings.BaseUrl is null || Environment.GetEnvironmentVariable("CONFORMANCE_REFERENCE_URL") is not { Length: > 0 })
        {
            Skip = "No reference to compare with: set CONFORMANCE_REFERENCE_URL (docs/conformance.md).";
        }
    }
}

// Against whatever lists the database holds (the real ones, say): 2,000 screenings built from the lists' own names
// (as listed, in lower case, with a letter dropped) and ordinary names with an address, through both implementations'
// streams, compared result for result. The fictional golden set can't hold every quirk of real data: this found two
// (a Turkish capital İ, and a logarithm one bit apart on a band's edge). Doesn't change state, and needs no base state.
[Collection("4 Reference")]
public sealed class ReferenceTests(ITestOutputHelper output)
{
    private static readonly HttpClient Reference = new()
    {
        BaseAddress = new Uri((Environment.GetEnvironmentVariable("CONFORMANCE_REFERENCE_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://localhost") + "/"),
        Timeout = TimeSpan.FromMinutes(10),
    };

    [ReferenceFact]
    public async Task EveryResult_OnTheListsInTheDatabase_IsTheReferences()
    {
        var names = new List<string>();
        foreach (var list in (await Suite.GetAsync("api/v1/sources")).GetProperty("lists").EnumerateArray().Where(list => list.GetProperty("state").GetString() != "NotImported"))
        {
            for (var offset = 0; offset < list.GetProperty("recordCount").GetInt32(); offset += 100)
            {
                using var page = await Suite.PostAsync($"api/v1/lists/{list.GetProperty("code").GetString()}/browse", new { offset, limit = 100 });
                names.AddRange(JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("primaryName").GetString()!));
            }
        }

        var rng = new Rng(Fixtures.Seed + 2);
        string[] people = ["Hannah Becker", "Lucas Moreau", "Ivan Petrov", "Olena Koval", "Maria Gonzalez"];
        var requests = Enumerable.Range(0, 2000).Select(i =>
        {
            if (i % 2 == 0)
            {
                return (object)new { reference = $"ref-{i}", receiverName = rng.Pick(people), address = "Lenina 5, Moscow" };
            }

            var name = rng.Pick(names);
            name = i % 3 == 0 ? name.ToLowerInvariant() : name;
            if (i % 5 == 0 && name.Length > 4)
            {
                name = name.Remove(1 + rng.Next(name.Length - 2), 1);
            }

            return new { reference = $"ref-{i}", companyName = name };
        }).ToList();
        var ndjson = string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request, Suite.Json))) + "\n";

        var (actual, actualTime) = await StreamAsync(Suite.Http, ndjson);
        var (expected, expectedTime) = await StreamAsync(Reference, ndjson);

        output.WriteLine($"{names.Count} names; {requests.Count} screenings: {actualTime.TotalMilliseconds / requests.Count:F2} ms each here, {expectedTime.TotalMilliseconds / requests.Count:F2} ms in the reference");
        var differences = expected.Zip(actual)
            .Where(pair => !JsonNode.DeepEquals(pair.First, pair.Second))
            .Select(pair => $"{pair.First?["reference"]}: {GoldenFiles.Difference(pair.First, pair.Second, "$")}")
            .ToList();
        Assert.True(expected.Count == actual.Count && differences.Count == 0,
            $"{differences.Count} of {expected.Count} differ from the reference:{Environment.NewLine}{string.Join(Environment.NewLine, differences.Take(25))}");
    }

    private static async Task<(List<JsonNode?> Results, TimeSpan Time)> StreamAsync(HttpClient client, string ndjson)
    {
        using var content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson");
        var clock = Stopwatch.StartNew();
        using var response = await client.PostAsync(new Uri("api/v1/screenings/stream", UriKind.Relative), content);
        var text = await response.Content.ReadAsStringAsync();
        clock.Stop();
        Assert.True(response.IsSuccessStatusCode, $"Stream: {(int)response.StatusCode}");
        var results = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)).Where(line => line?["type"]?.GetValue<string>() == "result").ToList();
        var end = JsonNode.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.False(end?["truncated"]?.GetValue<bool>(), "The stream stopped at its limit: raise Screening__StreamMaxRecords to 2000 or more.");
        return (results, clock.Elapsed);
    }
}
