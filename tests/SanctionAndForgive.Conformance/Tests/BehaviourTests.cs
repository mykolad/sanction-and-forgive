using System.Net;
using System.Text;
using System.Text.Json;
using SanctionAndForgive.Conformance.Feeds;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// What an implementation must do that the golden files don't pin: validation, the stream's framing and limits, the
// pages, health, sign-in, history, and never logging what was entered. Nothing here changes state.
[Collection("2 Behaviour")]
public sealed class BehaviourTests
{
    [ConformanceFact]
    public async Task Health_Readiness_AndTheOpenApiDescription_Answer()
    {
        await Suite.ReadyAsync();

        foreach (var path in new[] { "healthz", "readyz", "openapi/v1.json" })
        {
            using var response = await Suite.Http.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}");
        }
    }

    [ConformanceFact]
    public async Task EveryPage_IsServed_WithTheMenu()
    {
        await Suite.ReadyAsync();

        foreach (var page in new[] { "", "lists/", "status/", "allow-list/", "admin/" })
        {
            var html = await Suite.Http.GetStringAsync(new Uri(page, UriKind.Relative));
            Assert.Contains("<nav class=\"menu\"", html, StringComparison.Ordinal);
            foreach (var link in new[] { "href=\"/\"", "href=\"/lists\"", "href=\"/status\"", "href=\"/allow-list\"", "href=\"/admin\"" })
            {
                Assert.Contains(link, html, StringComparison.Ordinal);
            }
        }

        foreach (var asset in new[] { "app.js", "app.css", "lists/lists.js", "status/status.js", "admin/admin.js", "allow-list/allow-list.js" })
        {
            using var response = await Suite.Http.GetAsync(new Uri(asset, UriKind.Relative));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{asset}: {(int)response.StatusCode}");
        }
    }

    [ConformanceFact]
    public async Task AScreening_IsRefused_WithoutAValidReference_OrAnyField_OrWithAFieldTooLong_AndNeverEchoesIt()
    {
        await Suite.ReadyAsync();
        var marker = "Zyxwvut Conformance-Marker";

        var cases = new object[]
        {
            new { receiverName = marker },
            new { reference = "has space", receiverName = marker },
            new { reference = new string('a', 65), receiverName = marker },
            new { reference = "conformance-2" },
            new { reference = "conformance-3", receiverName = "", companyName = " ", address = "" },
            new { reference = "conformance-4", receiverName = marker + new string('x', 300) },
        };
        foreach (var body in cases)
        {
            using var response = await Suite.PostAsync("api/v1/screenings", body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(body, Suite.Json)[..Math.Min(80, JsonSerializer.Serialize(body, Suite.Json).Length)]}: {(int)response.StatusCode}");
            Assert.DoesNotContain("Zyxwvut", text, StringComparison.Ordinal);
        }
    }

    [ConformanceFact]
    public async Task AScreening_EchoesTheReference_ListsEveryList_AndCarriesTheDisclaimer_NeverClearingAnyone()
    {
        await Suite.ReadyAsync();

        var result = await Suite.ScreenAsync(new { reference = "TX-2026/11:09.a_b", companyName = "Greenfield Bakery Ltd" });

        Assert.Equal("TX-2026/11:09.a_b", result.GetProperty("reference").GetString());
        Assert.Equal(FeedFiles.Feeds.Count + 1, result.GetProperty("lists").GetArrayLength());
        Assert.Contains("not a legal determination", result.GetProperty("disclaimer").GetString(), StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{12}$", result.GetProperty("matcherVersion").GetString());
        var summary = result.GetProperty("summary").GetString()!;
        Assert.StartsWith("No match found in: ", summary, StringComparison.Ordinal);
        foreach (var word in new[] { "not sanctioned", "cleared", "forgiven", "absolved" })
        {
            Assert.DoesNotContain(word, summary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [ConformanceFact]
    public async Task TheStream_SkipsBlankLines_AnswersABadRecordWithAnError_AndEndsWithTheCounts()
    {
        await Suite.ReadyAsync();
        var ndjson = string.Join('\n',
            """{"reference":"S-1","companyName":"Volga Trade LLC"}""",
            string.Empty,
            """{"reference":"S-3"}""",
            """not json""",
            """{"reference":"S-5","receiverName":"Hannah Becker"}""") + "\n";

        using var content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson");
        using var response = await Suite.Http.PostAsync(new Uri("api/v1/screenings/stream", UriKind.Relative), content);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

        Assert.Equal(["header", "result", "error", "error", "result", "end"], lines.Select(line => line.GetProperty("type").GetString()));
        Assert.Equal([1, 3, 4, 5], lines[1..5].Select(line => line.GetProperty("line").GetInt32()));
        Assert.Equal("S-3", lines[2].GetProperty("reference").GetString());
        Assert.Equal(FeedFiles.Feeds.Count + 1, lines[0].GetProperty("lists").GetArrayLength());
        Assert.Equal((2, 2, false), (lines[^1].GetProperty("screened").GetInt32(), lines[^1].GetProperty("errors").GetInt32(), lines[^1].GetProperty("truncated").GetBoolean()));
    }

    [ConformanceFact]
    public async Task TheStream_StopsAtItsRecordLimit_AndSaysWhereToResume()
    {
        await Suite.ReadyAsync();
        // The run sets a small limit (Screening__StreamMaxRecords, docs/conformance.md); the header says what it is.
        var limit = (await Suite.StreamAsync([new { reference = "L-0", receiverName = "Hannah Becker" }]))[0].GetProperty("maxRecords").GetInt32();
        Assert.InRange(limit, 1, 5000);

        var lines = await Suite.StreamAsync(Enumerable.Range(1, limit + 5).Select(i => (object)new { reference = $"L-{i}", receiverName = "Hannah Becker" }));

        var end = lines[^1];
        Assert.Equal(("end", limit, true), (end.GetProperty("type").GetString(), end.GetProperty("screened").GetInt32(), end.GetProperty("truncated").GetBoolean()));
        Assert.Equal(limit + 1, end.GetProperty("nextLine").GetInt32());
    }

    [ConformanceFact]
    public async Task Browsing_AnUnknownList_Is404()
    {
        await Suite.ReadyAsync();

        using var response = await Suite.PostAsync("api/v1/lists/no-such-list/browse", new { offset = 0, limit = 10 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [ConformanceFact]
    public async Task EveryListsFullHistory_IsServed_WithWhatEachUpdateChanged()
    {
        await Suite.ReadyAsync();

        foreach (var (code, count) in FeedFiles.Feeds)
        {
            var body = await Suite.GetAsync($"api/v1/sources/{code}/history");
            var history = body.GetProperty("history").EnumerateArray().ToList();
            Assert.NotEmpty(history);
            Assert.All(history, row => Assert.True(row.TryGetProperty("added", out _) && row.TryGetProperty("removed", out _) && row.TryGetProperty("changed", out _)));
            var active = Assert.Single(history, row => row.GetProperty("result").GetString() == "Active");
            Assert.Equal(count, active.GetProperty("recordCount").GetInt32());
        }

        using var unknown = await Suite.Http.GetAsync(new Uri("api/v1/sources/no-such-list/history", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [ConformanceFact]
    public async Task SignIn_IsReadFromThePlatformsHeader()
    {
        await Suite.ReadyAsync();

        var anonymous = await Suite.GetAsync("api/v1/admin/me");
        using var response = await Suite.SendAsync(HttpMethod.Get, "api/v1/admin/me", body: null, Suite.Principal(Suite.AdminName, admin: true), fromThisSite: false);
        var admin = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        using var visitorResponse = await Suite.SendAsync(HttpMethod.Get, "api/v1/admin/me", body: null, Suite.Principal("visitor@example.invalid", admin: false), fromThisSite: false);
        var visitor = JsonSerializer.Deserialize<JsonElement>(await visitorResponse.Content.ReadAsStringAsync());

        Assert.Equal((true, false), (anonymous.GetProperty("enabled").GetBoolean(), anonymous.GetProperty("signedIn").GetBoolean()));
        Assert.Equal((Suite.AdminName, true), (admin.GetProperty("name").GetString(), admin.GetProperty("isAdmin").GetBoolean()));
        Assert.Equal((true, false), (visitor.GetProperty("signedIn").GetBoolean(), visitor.GetProperty("isAdmin").GetBoolean()));
    }

    [ConformanceFact]
    public async Task WhatWasEntered_IsNeverLogged_ButEachScreeningIsAudited_ByReference()
    {
        if (Settings.ApiLogs.Count == 0)
        {
            return;
        }

        await Suite.ReadyAsync();
        var run = Guid.NewGuid().ToString("N")[..16];
        var single = $"single-{run}";
        var streamed = $"stream-{run}";

        await Suite.ScreenAsync(new { reference = single, receiverName = "Qwertyuiop Logmarker", companyName = "Asdfghjkl Logmarker LLC", address = "Zxcvbnm Street 1, Logmarkertown" });
        await Suite.StreamAsync([new { reference = streamed, receiverName = "Qwertyuiop Logmarker" }]);
        await Task.Delay(1000);

        // Standard output and error both: input logged to either is a leak. The files are still open: read them shared.
        var text = new StringBuilder();
        foreach (var log in Settings.ApiLogs.Where(File.Exists))
        {
            await using var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            text.Append(await reader.ReadToEndAsync()).Append('\n');
        }

        // One audit line per screening ("Screening <reference>: <outcome>; …", docs/api.md), under its own reference.
        var lines = text.ToString().Split('\n');
        Assert.Single(lines, line => line.Contains($"Screening {single}:", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains($"Screening {streamed}:", StringComparison.Ordinal));
        Assert.DoesNotContain("Logmarker", text.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Qwertyuiop", text.ToString(), StringComparison.Ordinal);
    }
}
