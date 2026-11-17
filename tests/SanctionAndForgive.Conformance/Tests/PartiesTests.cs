using System.Net;
using System.Text.Json;
using SanctionAndForgive.Conformance.Feeds;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// Several parties of a shipment in one screening (#65; version 2, the Rust reference first): each party's name and
// address are entries tagged with its number and role, every list answers each, and the outcome is the highest.
[Collection("2 Behaviour")]
public sealed class PartiesTests
{
    [SinceVersionFact(2)]
    public async Task TheStatus_AdvertisesParties()
    {
        await Suite.ReadyAsync();

        var status = await Suite.GetAsync("api/v1/status");

        Assert.Contains("parties", status.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
    }

    [SinceVersionFact(2)]
    public async Task EveryParty_IsScreened_AndTaggedWithItsNumberAndRole_TheOutcomeTheHighest()
    {
        await Suite.ReadyAsync();
        var listed = FeedFiles.Subjects(FeedFiles.Feeds[0].Code).First(subject => !subject.Person);

        var result = await Suite.ScreenAsync(new
        {
            companyName = "Greenfield Bakery Ltd",
            parties = new object[]
            {
                new { role = "shipper", name = "Pinecrest Orchard Supplies Ltd", kind = "company", address = "12 Harbour Road, Bristol, United Kingdom" },
                new { role = "consignee", name = listed.LatinName },
                new { role = "notifyParty", name = "Quentin Marlowe-Fairbanks", kind = "person" },
            },
        });

        var entries = result.GetProperty("entries").EnumerateArray().ToList();
        string Tag(JsonElement entry) => entry.TryGetProperty("party", out var party)
            ? $"{party.GetInt32()} {entry.GetProperty("role").GetString()} {entry.GetProperty("kind").GetString()}"
            : $"- {entry.GetProperty("kind").GetString()}";
        Assert.Equal(
            ["- Company", "1 shipper Address", "1 shipper Company", "2 consignee Company", "3 notifyParty Receiver"],
            entries.Select(Tag).Order(StringComparer.Ordinal));
        Assert.Equal("Match", result.GetProperty("outcome").GetString());
        Assert.All(entries, entry => Assert.Equal(FeedFiles.Feeds.Count + 1, entry.GetProperty("cells").GetArrayLength()));
        var consignee = entries.Single(entry => entry.TryGetProperty("party", out var party) && party.GetInt32() == 2);
        Assert.Contains(consignee.GetProperty("cells").EnumerateArray(), cell => cell.GetProperty("outcome").GetString() == "Match");
    }

    [SinceVersionFact(2)]
    public async Task AParty_IsRefused_WithoutAKnownRole_OrANameOrAddress_AndAtMostTenAreTaken()
    {
        await Suite.ReadyAsync();
        var party = new { role = "shipper", name = "Pinecrest Orchard Supplies Ltd" };

        foreach (var body in new object[]
        {
            new { reference = "parties-1", parties = new object[] { new { role = "captain", name = "Pinecrest Orchard Supplies Ltd" } } },
            new { reference = "parties-2", parties = new object[] { new { role = "shipper", kind = "company" } } },
            new { reference = "parties-3", parties = new object[] { new { role = "shipper", name = "Pinecrest", kind = "vessel" } } },
            new { reference = "parties-4", parties = Enumerable.Repeat<object>(party, 11).ToArray() },
        })
        {
            using var response = await Suite.PostAsync("api/v1/screenings", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("Pinecrest", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Parties alone are enough.
        var result = await Suite.ScreenAsync(new { reference = "parties-5", parties = new object[] { party } });
        Assert.Equal(1, result.GetProperty("entries")[0].GetProperty("party").GetInt32());
    }

    [SinceVersionFact(2)]
    public async Task TheStream_TagsEachPartysEntries()
    {
        await Suite.ReadyAsync();

        var lines = await Suite.StreamAsync([new { reference = "parties-stream", parties = new object[] { new { role = "forwarder", name = "Pinecrest Orchard Supplies Ltd" } } }]);

        var entry = lines.Single(line => line.GetProperty("type").GetString() == "result").GetProperty("entries")[0];
        Assert.Equal((1, "forwarder"), (entry.GetProperty("party").GetInt32(), entry.GetProperty("role").GetString()));
    }
}
