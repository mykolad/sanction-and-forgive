using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SanctionAndForgive.Api.Admin;
using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Api.ApiFactory;

namespace SanctionAndForgive.Tests.Api;

// The allow-list in screening responses, streams and the audit line, and its guard against allow-listing a listed party.
public sealed class AllowListApiTests : IDisposable
{
    private readonly ApiFactory factory = new();
    private readonly HttpClient client;

    public AllowListApiTests()
    {
        client = factory.CreateClient();
        factory.Load(
            Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)),
            Feed("ofac-sdn", "OFAC SDN", Now, Subject("AB Incorporated", SubjectKind.Entity)));
        factory.Catalog.ReplaceAllowList(AllowList.Build([new AllowListEntry(5, "AB Corp", "A customer since 2019, not the listed company")], "3"));
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task AnAllowedPossibleMatch_IsShown_DoesntRaiseTheOutcome_AndTheVersionComesBack()
    {
        using var response = await client.PostAsJsonAsync("/api/v1/screenings", new { reference = "TX-A1", companyName = "AB Corp" });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("NoMatch", result.GetProperty("outcome").GetString());
        Assert.Equal("3", result.GetProperty("allowListVersion").GetString());
        var entry = result.GetProperty("entries")[0];
        Assert.Equal(5, entry.GetProperty("allowedBy").GetProperty("id").GetInt32());
        var ofac = entry.GetProperty("cells").EnumerateArray().Single(cell => cell.GetProperty("list").GetString() == "ofac-sdn");
        Assert.Equal(("Review", true), (ofac.GetProperty("outcome").GetString(), ofac.GetProperty("allowed").GetBoolean()));
        var summary = result.GetProperty("summary").GetString()!;
        Assert.Contains("allowed by the allow-list", summary, StringComparison.Ordinal);
        Assert.Contains("No match found in:", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("cleared", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not sanctioned", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAuditLine_SaysTheAllowListVersion_AndTheEntryById_NeverTheName()
    {
        using var response = await client.PostAsJsonAsync("/api/v1/screenings", new { reference = "TX-A2", companyName = "AB Corp" });

        var audit = Assert.Single(factory.Logs.GetSnapshot(), record => record.Message.Contains("TX-A2", StringComparison.Ordinal)).Message;
        Assert.Contains("allow-list 3", audit, StringComparison.Ordinal);
        Assert.Contains("(allow-list entry 5)", audit, StringComparison.Ordinal);
        Assert.Contains("ofac-sdn Review allowed", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("AB Corp", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStream_MarksAllowedCells_AndTheEntryById()
    {
        using var content = new StringContent("{\"reference\":\"TX-A3\",\"companyName\":\"AB Corp\"}\n", Encoding.UTF8, "application/x-ndjson");
        using var response = await client.PostAsync(new Uri("/api/v1/screenings/stream", UriKind.Relative), content);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

        Assert.Equal("3", lines[0].GetProperty("allowListVersion").GetString());
        Assert.Equal("NoMatch", lines[1].GetProperty("outcome").GetString());
        var entry = lines[1].GetProperty("entries")[0];
        Assert.Equal(5, entry.GetProperty("allowListEntryId").GetInt32());
        Assert.Contains(entry.GetProperty("cells").EnumerateArray(), cell => cell.GetProperty("allowed").GetBoolean());
        Assert.DoesNotContain("AB Corp", lines[1].GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileAListCantBeChecked_NothingIsAdded()
    {
        factory.Load(
            Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)),
            Feed("uk", "UK", Now.AddHours(-80), Subject("Volga Trade", SubjectKind.Entity)));
        var admin = factory.Services.GetRequiredService<IAllowListAdmin>();

        var result = await admin.AddAsync("Volga Trade", "test", "owner@example.invalid", new HashSet<string>(), CancellationToken.None);

        Assert.True(result.Unavailable);
        Assert.Null(result.Id);
        Assert.Equal(0, factory.Database.Opened);
    }

    [Fact]
    public async Task AListedPartysOwnName_CantBeAllowListed_AndNothingIsWritten()
    {
        var admin = factory.Services.GetRequiredService<IAllowListAdmin>();

        var result = await admin.AddAsync("Romashka LLC", "test", "owner@example.invalid", new HashSet<string>(["eu/X-12-R"]), CancellationToken.None);

        Assert.Null(result.Id);
        Assert.Equal("EU: Romashka LLC", result.ListedAs);
        Assert.Equal(0, factory.Database.Opened);
    }

    [Fact]
    public async Task ANameAlreadyInForce_IsNotAddedTwice_HoweverItsWritten()
    {
        var admin = factory.Services.GetRequiredService<IAllowListAdmin>();

        var result = await admin.AddAsync("ab corp.", "test", "owner@example.invalid", new HashSet<string>(["ofac-sdn/X-15-A"]), CancellationToken.None);

        Assert.Equal((null, 5), (result.Id, result.ExistingId));
        Assert.Equal(0, factory.Database.Opened);
    }

    [Fact]
    public async Task ANameWithPossibleMatches_ListsThem_AndIsNotAddedUntilEachIsConfirmed()
    {
        var admin = factory.Services.GetRequiredService<IAllowListAdmin>();

        var unconfirmed = await admin.AddAsync("AB Corporation", "test", "owner@example.invalid", new HashSet<string>(), CancellationToken.None);
        var otherKey = await admin.AddAsync("AB Corporation", "test", "owner@example.invalid", new HashSet<string>(["eu/X-12-R"]), CancellationToken.None);

        Assert.Null(unconfirmed.Id);
        Assert.Null(unconfirmed.ListedAs);
        var hit = Assert.Single(unconfirmed.PossibleMatches);
        Assert.Equal(("ofac-sdn/X-15-A", "OFAC SDN", "AB Incorporated"), (hit.Key, hit.ListName, hit.ListedName));
        Assert.Contains(EntryKind.Company, hit.ReadAs);
        Assert.Null(otherKey.Id);
        Assert.Single(otherKey.PossibleMatches);
        Assert.Equal(0, factory.Database.Opened);
    }
}
