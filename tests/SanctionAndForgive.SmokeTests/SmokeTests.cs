using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SanctionAndForgive.SmokeTests;

// Run by the deploy workflow against the new revision: SMOKE_BASE_URL, and optionally SMOKE_EXPECTED_VERSION and
// SMOKE_EXPECTED_REVISION. They check that it serves and answers, not what the lists say: a first deploy has no
// lists yet (every row Not checked) until the import job has run.
public sealed class SmokeTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(6);
    private readonly HttpClient client;

    public SmokeTests()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SMOKE_BASE_URL");
        Assert.False(string.IsNullOrEmpty(baseUrl), "Set SMOKE_BASE_URL to the deployed app.");
        client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose() => client.Dispose();

    [Fact]
    public async Task NewRevision_IsLive_AndReady()
    {
        var version = Environment.GetEnvironmentVariable("SMOKE_EXPECTED_VERSION");
        var revision = Environment.GetEnvironmentVariable("SMOKE_EXPECTED_REVISION");
        var health = await WaitForAsync("/healthz", body =>
            (string.IsNullOrEmpty(version) || body.GetProperty("version").GetString() == version)
            && (string.IsNullOrEmpty(revision) || body.GetProperty("revision").GetString() == revision));
        Assert.Equal("ok", health.GetProperty("status").GetString());

        var ready = await WaitForAsync("/readyz", body => body.GetProperty("status").GetString() == "ready");
        Assert.Equal("ready", ready.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Page_AndItsScript_AreServed()
    {
        var page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        using var script = await client.GetAsync(new Uri("/app.js", UriKind.Relative));

        Assert.Contains("id=\"screen-form\"", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
    }

    [Fact]
    public async Task Screening_AnswersWithEveryList_AndTheDisclaimer()
    {
        await WaitForAsync("/readyz", body => body.GetProperty("status").GetString() == "ready");

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/screenings", UriKind.Relative), new { reference = "smoke-" + Guid.NewGuid().ToString("N")[..12], companyName = "Zephyrine Quillbrook Smoke Test Holding" });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(result.GetProperty("outcome").GetString(), (string[])["NoMatch", "Incomplete"]);
        Assert.Contains("not a legal determination", result.GetProperty("disclaimer").GetString(), StringComparison.Ordinal);
        var status = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/status", UriKind.Relative));
        Assert.Equal(status.GetProperty("lists").GetArrayLength(), result.GetProperty("lists").GetArrayLength());
    }

    [Fact]
    public async Task Stream_AnswersEachLine_ThroughTheIngress()
    {
        await WaitForAsync("/readyz", body => body.GetProperty("status").GetString() == "ready");
        var id = Guid.NewGuid().ToString("N")[..12];
        using var content = new StringContent($"{{\"reference\":\"smoke-{id}-1\",\"companyName\":\"Zephyrine Quillbrook Smoke Test Holding\"}}\n{{\"reference\":\"smoke-{id}-2\",\"receiverName\":\"Quillon Zephyrsmith\"}}\n", System.Text.Encoding.UTF8, "application/x-ndjson");

        using var response = await client.PostAsync(new Uri("/api/v1/screenings/stream", UriKind.Relative), content);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["header", "result", "result", "end"], lines.Select(line => line.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Examples_AndOpenApi_Answer()
    {
        await WaitForAsync("/readyz", body => body.GetProperty("status").GetString() == "ready");

        using var examples = await client.GetAsync(new Uri("/api/v1/examples", UriKind.Relative));
        using var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, examples.StatusCode);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
    }

    /// <summary>Polls until the endpoint answers 200 with a body that passes <paramref name="accept"/>.</summary>
    private async Task<JsonElement> WaitForAsync(string path, Func<JsonElement, bool> accept)
    {
        var deadline = DateTime.UtcNow + Patience;
        string last = "no answer";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
                var text = await response.Content.ReadAsStringAsync();
                last = $"{(int)response.StatusCode} {text}";
                if (response.IsSuccessStatusCode)
                {
                    var body = JsonDocument.Parse(text).RootElement.Clone();
                    if (accept(body))
                    {
                        return body;
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = exception.GetType().Name;
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        Assert.Fail($"{path} didn't become ready within {Patience.TotalMinutes} minutes; last answer: {last}");
        return default;
    }
}
