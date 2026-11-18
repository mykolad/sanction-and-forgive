using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using SanctionAndForgive.Conformance.Feeds;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// Vessels (#69; version 2, the Rust reference first). Every fictional feed that lists ships lists one, CONFORMANCE STAR,
// IMO 1234567: version 1 leaves it out, version 2 keeps it in each list's vessels, searched only by a vessel entry.
[Collection("2 Behaviour")]
public sealed class VesselsTests
{
    private const string Imo = "1234567";

    [SinceVersionFact(2)]
    public async Task TheStatus_AdvertisesVessels()
    {
        await Suite.ReadyAsync();

        var status = await Suite.GetAsync("api/v1/status");

        Assert.Contains("vessels", status.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
    }

    [SinceVersionFact(2)]
    public async Task AVessel_IsFoundByItsName_OnEveryListOfShips_AndOnlyByAVesselEntry()
    {
        await Suite.ReadyAsync();

        var result = await Suite.ScreenAsync(new { reference = "vessel-name", vesselName = "M/V Conformance Star" });

        var entry = result.GetProperty("entries")[0];
        Assert.Equal(("Vessel", "M/V Conformance Star"), (entry.GetProperty("kind").GetString(), entry.GetProperty("input").GetString()));
        foreach (var list in new[] { "ofac-sdn", "uk", "fr-tresor", "nz-mfat", "ca-sema", "ch-seco" })
        {
            var cell = Suite.Cell(result, list)!.Value;
            Assert.True(cell.GetProperty("outcome").GetString() == "Match", $"{list}: {cell}");
            Assert.Equal("Vessel", cell.GetProperty("bestCandidate").GetProperty("type").GetString());
        }

        // A list without ships is searched and finds none.
        Assert.Equal("NoMatch", Suite.Cell(result, "eu")!.Value.GetProperty("outcome").GetString());

        // The same words as a company's name never find the ship.
        var company = await Suite.ScreenAsync(new { reference = "vessel-as-company", companyName = "Conformance Star" });
        foreach (var cell in company.GetProperty("entries")[0].GetProperty("cells").EnumerateArray())
        {
            if (cell.TryGetProperty("bestCandidate", out var best) && best.ValueKind == JsonValueKind.Object)
            {
                Assert.NotEqual("Vessel", best.GetProperty("type").GetString());
            }
        }
    }

    [SinceVersionFact(2)]
    public async Task AnImoNumber_IsAMatch_WhateverTheName_AndOneThatIsNoImoNumberIsRefused()
    {
        await Suite.ReadyAsync();

        var result = await Suite.ScreenAsync(new { reference = "vessel-imo", vesselName = "Renamed Since", vesselImoNumber = $"IMO {Imo}" });

        Assert.Equal("Match", result.GetProperty("outcome").GetString());
        var cell = Suite.Cell(result, "uk")!.Value;
        Assert.Equal(("Match", 100), (cell.GetProperty("outcome").GetString(), cell.GetProperty("percent").GetInt32()));
        Assert.StartsWith($"Identifier: equals the listed IMO number {Imo}", cell.GetProperty("bestCandidate").GetProperty("alignment")[0].GetString(), StringComparison.Ordinal);

        var alone = await Suite.ScreenAsync(new { reference = "vessel-imo-alone", vesselImoNumber = Imo });
        Assert.Equal(($"IMO {Imo}", "Match"), (alone.GetProperty("entries")[0].GetProperty("input").GetString(), Suite.Cell(alone, "ofac-sdn")!.Value.GetProperty("outcome").GetString()));

        // A slip of a digit fails the check digit; the value is never echoed.
        foreach (var body in new object[]
        {
            new { reference = "vessel-bad-1", vesselImoNumber = "1234568" },
            new { reference = "vessel-bad-2", vesselImoNumber = "IMO 12345" },
        })
        {
            using var response = await Suite.PostAsync("api/v1/screenings", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("12345", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [SinceVersionFact(2)]
    public async Task Browsing_ShowsAListsVessels()
    {
        await Suite.ReadyAsync();

        using var response = await Suite.PostAsync("api/v1/lists/uk/browse", new { type = "Vessel", offset = 0, limit = 10 });

        response.EnsureSuccessStatusCode();
        var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var entries = page.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(("SHIP0001", "Vessel"), (entries.Single().GetProperty("externalId").GetString(), entries.Single().GetProperty("type").GetString()));
    }
}

// A list that lists ships, imported before ships were kept (#69): its vessel cells are NotChecked, never NoMatch, and
// the summary says so. Imported as a UK file without its ship, then the base files again.
[Collection("3 Changes")]
public sealed class VesselsBeforeShipsWereKeptTests
{
    [SinceVersionFact(2)]
    public async Task AListImportedWithoutItsShips_LeavesTheVesselNotChecked_AndTheSummarySaysSo()
    {
        await Suite.ReadyAsync();
        var document = XDocument.Load(Suite.FeedPath("uk", "base"));
        document.Root!.Elements("Designation").Single(designation => (string?)designation.Element("UniqueID") == "SHIP0001").Remove();
        var path = Suite.FeedPath("uk", "without-ships");
        document.Save(path);
        try
        {
            var import = await Suite.CliAsync($"import --source uk --file {Suite.Quote(path)}");
            Assert.True(import.ExitCode == 0, import.Output);
            await Suite.WaitForListsAsync(lists => lists["uk"].GetProperty("recordCount").GetInt32() == FeedFiles.RecordCount("uk") - 1);

            var result = await Suite.ScreenAsync(new { reference = "vessel-old-import", vesselName = "Conformance Star" });

            Assert.Equal("NotChecked", Suite.Cell(result, "uk")!.Value.GetProperty("state").GetString());
            Assert.Equal("Match", Suite.Cell(result, "ofac-sdn")!.Value.GetProperty("outcome").GetString());
            var summary = result.GetProperty("summary").GetString()!;
            Assert.Contains("The vessel wasn't checked in: UK", summary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            await Suite.RestoreBaseAsync();
        }
    }
}
