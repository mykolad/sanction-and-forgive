using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// Destinations under broad restrictions (#67; version 2, the Rust reference first): a table administrators keep, read
// in every address entered; a notice raises the outcome to at least Review. A test that needs a table that never had a
// row deletes them first; every test leaves no row in force.
[Collection("3 Changes")]
public sealed class DestinationsTests
{
    /// <summary>A table that never had a row (the starting rows go only into one). Deleted only once every row has been
    /// removed and the API has seen it (<see cref="EmptyTableAsync"/>): the check for changes counts rows and removals,
    /// so going straight back to no rows from rows it never saw would look like no change at all. In production rows
    /// are never deleted, so both counts only grow.</summary>
    private static async Task FreshTableAsync()
    {
        await EmptyTableAsync();
        await using (var connection = await Suite.OpenSqlAsync())
        {
            await Suite.ExecuteAsync(connection, "DELETE FROM RestrictedDestinations");
        }

        await WaitForTableAsync(table => table.GetProperty("destinations").GetArrayLength() == 0 && table.GetProperty("version").GetString() == "0");
    }

    /// <summary>No row in force, as an administrator's removals leave it, once the API has read it.</summary>
    private static async Task EmptyTableAsync()
    {
        int left;
        await using (var connection = await Suite.OpenSqlAsync())
        {
            await Suite.ExecuteAsync(connection, "UPDATE RestrictedDestinations SET RemovedAt = SYSDATETIMEOFFSET(), RemovedBy = 'conformance' WHERE RemovedAt IS NULL");
            left = await Suite.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM RestrictedDestinations");
        }

        var version = (left + left).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await WaitForTableAsync(table => table.GetProperty("destinations").GetArrayLength() == 0 && table.GetProperty("version").GetString() == version);
    }

    /// <summary>Waits (up to 30 s: the API checks for changes every second in a conformance run) until the public table
    /// satisfies <paramref name="condition"/>.</summary>
    private static async Task<JsonElement> WaitForTableAsync(Func<JsonElement, bool> condition)
    {
        for (var attempt = 0; ; attempt++)
        {
            var table = await Suite.GetAsync("api/v1/destinations");
            if (condition(table))
            {
                return table;
            }

            Assert.True(attempt < 60, $"The table of destinations didn't reach the state expected: {table}");
            await Task.Delay(500);
        }
    }

    private static JsonElement? Destination(JsonElement result) =>
        result.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "Address" && entry.TryGetProperty("destination", out _))
            .Select(entry => (JsonElement?)entry.GetProperty("destination")).FirstOrDefault();

    private static async Task<(string Outcome, string? State, string? Code, string Summary)> ScreenAddressAsync(string address)
    {
        var result = await Suite.ScreenAsync(new { reference = "destinations-1", address });
        var destination = Destination(result);
        return (result.GetProperty("outcome").GetString()!, destination?.GetProperty("state").GetString(), destination?.GetProperty("code").GetString(),
            result.GetProperty("summary").GetString()!);
    }

    private static object Row(string code, string kind, string name, string status, string? parent = null, string[]? otherNames = null, string country = "UA") => new
    {
        code,
        kind,
        name,
        otherNames = otherNames ?? [],
        country,
        parent,
        status,
        restrictedBy = status == "Outside" ? "" : "EU, UK",
        legalSource = status == "Outside" ? "" : "Conformance regulation 1/2026",
        asOf = "2026-11-18",
    };

    [SinceVersionFact(2)]
    public async Task TheStatus_AdvertisesDestinations_AndAnEmptyTableRaisesNothing()
    {
        await Suite.ReadyAsync();
        await EmptyTableAsync();

        var status = await Suite.GetAsync("api/v1/status");
        Assert.Contains("destinations", status.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));

        var (_, state, _, _) = await ScreenAddressAsync("ul. Kirova 10, Simferopol, Russia");
        Assert.Null(state);
    }

    [SinceVersionFact(2)]
    public async Task EditingTheTable_NeedsTheRoleAndTheSitesHeader()
    {
        await Suite.ReadyAsync();
        var cases = new (string? Principal, bool FromThisSite, HttpStatusCode Expected)[]
        {
            (null, true, HttpStatusCode.Unauthorized),
            (Suite.Principal("visitor@example.invalid", admin: false), true, HttpStatusCode.Forbidden),
            (Suite.Principal(Suite.AdminName, admin: true), false, HttpStatusCode.BadRequest),
        };

        foreach (var (principal, fromThisSite, expected) in cases)
        {
            using var add = await Suite.SendAsync(HttpMethod.Post, "api/v1/admin/destinations", Row("xx-a", "Region", "Atlantis", "Restricted"), principal, fromThisSite);
            using var start = await Suite.SendAsync(HttpMethod.Post, "api/v1/admin/destinations/start", body: null, principal, fromThisSite);
            using var remove = await Suite.SendAsync(HttpMethod.Post, "api/v1/admin/destinations/1/remove", body: null, principal, fromThisSite);
            Assert.Equal((expected, expected, expected), (add.StatusCode, start.StatusCode, remove.StatusCode));
        }
    }

    /// <summary>The starting rows, loaded once into an empty table: an address in a restricted area, or in a partly
    /// restricted region with no place the table knows, is at least Review, whatever country it names; a settlement
    /// outside the restricted area clears its region's notice.</summary>
    [SinceVersionFact(2)]
    public async Task TheStartingRows_RaiseANotice_WhereTheOwnerDecided()
    {
        await Suite.ReadyAsync();
        await FreshTableAsync();
        try
        {
            using (var started = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations/start", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            }

            using (var again = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations/start", body: null))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            }

            var table = await WaitForTableAsync(table => table.GetProperty("destinations").GetArrayLength() > 60);
            Assert.Contains(table.GetProperty("destinations").EnumerateArray(), row => row.GetProperty("code").GetString() == "ua-crimea");
            Assert.DoesNotContain(table.GetProperty("destinations").EnumerateArray(), row => row.GetProperty("name").GetString() == "Syria");

            var simferopol = await ScreenAddressAsync("ul. Kirova 10, Simferopol, Russia");
            Assert.Equal(("Review", "restricted", "ua-simferopol"), (simferopol.Outcome, simferopol.State, simferopol.Code));
            Assert.Contains("Destination under broad restrictions", simferopol.Summary, StringComparison.Ordinal);

            var kramatorsk = await ScreenAddressAsync("Kramatorsk, Donetsk Oblast, Ukraine");
            Assert.Equal(("Review", "possiblyRestricted", "ua-donetsk-oblast"), (kramatorsk.Outcome, kramatorsk.State, kramatorsk.Code));

            var kherson = await ScreenAddressAsync("vul. Ushakova 5, Kherson, Khersonska oblast");
            Assert.Null(kherson.State);

            // A party's address too, and the stream.
            var shipment = await Suite.ScreenAsync(new { reference = "destinations-2", parties = new object[] { new { role = "consignee", name = "Pinecrest Orchard Supplies Ltd", address = "Pyongyang, DPRK" } } });
            Assert.Equal(("Review", "kp"), (shipment.GetProperty("outcome").GetString(), Destination(shipment)?.GetProperty("code").GetString()));

            var lines = await Suite.StreamAsync([new { reference = "destinations-stream", address = "Calle 23, La Habana, Cuba" }]);
            Assert.Equal(table.GetProperty("version").GetString(), lines[0].GetProperty("destinationsVersion").GetString());
            var line = lines.Single(l => l.GetProperty("type").GetString() == "result");
            Assert.Equal("Review", line.GetProperty("outcome").GetString());
            Assert.Equal("cu", line.GetProperty("entries")[0].GetProperty("destination").GetProperty("code").GetString());
        }
        finally
        {
            await EmptyTableAsync();
        }
    }

    /// <summary>An administrator's changes: a row added, changed (a new row with its code, the old one kept as history),
    /// removed; a settlement's region must be in the table; a name read as another place needs confirming.</summary>
    [SinceVersionFact(2)]
    public async Task ARow_IsAdded_Changed_AndRemoved_NeverDeleted()
    {
        await Suite.ReadyAsync();
        await FreshTableAsync();
        try
        {
            using (var orphan = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations", Row("xx-town", "Settlement", "Hollowmere", "Restricted", parent: "xx-region")))
            {
                Assert.Equal(HttpStatusCode.BadRequest, orphan.StatusCode);
            }

            foreach (var invalid in new[]
            {
                Row("Not A Code", "Region", "Atlantis", "Restricted"),
                Row("xx-a", "Planet", "Atlantis", "Restricted"),
                Row("xx-a", "Region", "Atlantis", "Maybe"),
                Row("xx-a", "Region", "Atlantis", "Restricted", country: "ZZ"),
            })
            {
                using var refused = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations", invalid);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }

            using (var region = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations", Row("xx-region", "Region", "Hollow Vale Oblast", "Partly", otherNames: ["Hollow Vale region"])))
            {
                Assert.Equal(HttpStatusCode.OK, region.StatusCode);
            }

            using (var outside = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations", Row("xx-town", "Settlement", "Hollowmere", "Outside", parent: "xx-region")))
            {
                Assert.Equal(HttpStatusCode.OK, outside.StatusCode);
            }

            await WaitForTableAsync(table => table.GetProperty("destinations").GetArrayLength() == 2);
            Assert.Equal(("Review", "possiblyRestricted"), await StateAsync("Elmfield, Hollow Vale Oblast"));
            Assert.Equal((null, null), await StateAsync("Hollowmere, Hollow Vale Oblast"));

            // A change: the same code, now restricted; the old row stays as history, removed.
            using (var changed = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations", Row("xx-town", "Settlement", "Hollowmere", "Restricted", parent: "xx-region")))
            {
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
                Assert.Equal(JsonValueKind.Number, (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replaced").ValueKind);
            }

            var current = await WaitForTableAsync(table => table.GetProperty("destinations").EnumerateArray().Any(row => row.GetProperty("code").GetString() == "xx-town" && row.GetProperty("status").GetString() == "Restricted"));
            Assert.Equal(("Review", "restricted"), await StateAsync("Hollowmere, Hollow Vale Oblast"));

            // Moscow is a place in another country: confirmed, or refused.
            using (var conflict = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/destinations", Row("xx-moscow", "Settlement", "Moscow", "Restricted", parent: "xx-region")))
            {
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                Assert.Equal("confirm", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }

            var town = current.GetProperty("destinations").EnumerateArray().Single(row => row.GetProperty("code").GetString() == "xx-town").GetProperty("id").GetInt32();
            using (var removed = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/destinations/{town}/remove", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            }

            using (var twice = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/destinations/{town}/remove", body: null))
            {
                Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
            }

            Assert.Equal(("Review", "possiblyRestricted"), await StateAsync("Hollowmere, Hollow Vale Oblast"));
            using var history = await Suite.AdminAsync(HttpMethod.Get, "api/v1/admin/destinations", body: null);
            var rows = (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(2, rows.Count(row => row.GetProperty("code").GetString() == "xx-town" && row.GetProperty("removedAt").ValueKind == JsonValueKind.String));
            Assert.All(rows, row => Assert.Equal(Suite.AdminName, row.GetProperty("addedBy").GetString()));
        }
        finally
        {
            await EmptyTableAsync();
        }
    }

    private static async Task<(string? Outcome, string? State)> StateAsync(string address)
    {
        var (outcome, state, _, _) = await ScreenAddressAsync(address);
        return (state is null ? null : outcome, state);
    }
}
