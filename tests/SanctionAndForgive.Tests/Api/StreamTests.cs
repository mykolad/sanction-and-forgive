using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SanctionAndForgive.Api.Screening;
using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Api.ApiFactory;

namespace SanctionAndForgive.Tests.Api;

// POST /api/v1/screenings/stream: NDJSON in, NDJSON out.
public sealed class StreamTests : IDisposable
{
    private readonly ApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    private void Load() => factory.Load(
        Feed("eu", "EU", Now.AddHours(-1), Subject("Romashka LLC", SubjectKind.Entity), Subject("Taras Shevchenko", SubjectKind.Individual)),
        Feed("uk", "UK", Now.AddHours(-80), Subject("Volga Trade", SubjectKind.Entity)));

    private static async Task<List<JsonElement>> StreamAsync(HttpClient client, string ndjson)
    {
        using var content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson");
        using var response = await client.PostAsync("/api/v1/screenings/stream", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    [Fact]
    public async Task Records_AnswerOneLineEach_WithHeaderAndEnd()
    {
        Load();
        using var client = factory.CreateClient();

        var lines = await StreamAsync(client, """
            {"reference":"TX-1","companyName":"ТОВ Ромашка"}

            {"reference":"TX-2","receiverName":"Шевченко Т.Г."}
            {"reference":"TX-3","companyName":"Zephyrine Quillbrook Ltd"}
            """);

        Assert.Equal(["header", "result", "result", "result", "end"], lines.Select(line => line.GetProperty("type").GetString()));
        Assert.Equal(2, lines[0].GetProperty("lists").GetArrayLength());
        Assert.Equal(["TX-1", "TX-2", "TX-3"], lines[1..4].Select(line => line.GetProperty("reference").GetString()));
        Assert.Equal([1, 3, 4], lines[1..4].Select(line => line.GetProperty("line").GetInt32()));
        Assert.Equal("Match", lines[1].GetProperty("outcome").GetString());
        Assert.Equal("Review", lines[2].GetProperty("outcome").GetString());
        var eu = lines[1].GetProperty("entries")[0].GetProperty("cells").EnumerateArray().Single(cell => cell.GetProperty("list").GetString() == "eu");
        Assert.Equal("Romashka LLC", eu.GetProperty("best").GetProperty("listedName").GetString());
        var uk = lines[1].GetProperty("entries")[0].GetProperty("cells").EnumerateArray().Single(cell => cell.GetProperty("list").GetString() == "uk");
        Assert.Equal(("NotChecked", JsonValueKind.Null), (uk.GetProperty("state").GetString(), uk.GetProperty("percent").ValueKind));
        Assert.Equal((3, 0, false), (lines[^1].GetProperty("screened").GetInt32(), lines[^1].GetProperty("errors").GetInt32(), lines[^1].GetProperty("truncated").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, lines[^1].GetProperty("nextLine").ValueKind);
    }

    [Fact]
    public async Task BadRecords_AreErrorLines_TheStreamGoesOn_AndInputIsNeverEchoedOrLogged()
    {
        Load();
        using var client = factory.CreateClient();
        var longLine = "{\"reference\":\"TX-9\",\"companyName\":\"" + new string('Ж', SanctionAndForgive.Api.Controllers.ScreeningStreamController.MaxLineBytes) + "\"}";

        var lines = await StreamAsync(client, string.Join('\n',
            "{\"companyName\":\"Ромашка без номера\"}",
            "not json at all: Шевченко",
            longLine,
            "{\"reference\":\"TX-4\",\"companyName\":\"Romashka\"}"));

        Assert.Equal(["header", "error", "error", "error", "result", "end"], lines.Select(line => line.GetProperty("type").GetString()));
        Assert.True(lines[1].GetProperty("errors").TryGetProperty("Reference", out _));
        Assert.Equal((1, 3), (lines[^1].GetProperty("screened").GetInt32(), lines[^1].GetProperty("errors").GetInt32()));
        var everything = string.Join('\n', lines.Select(line => line.GetRawText())) + string.Join('\n', factory.Logs.GetSnapshot().Select(record => record.Message));
        Assert.DoesNotContain("Ромашка без номера", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Шевченко", everything, StringComparison.Ordinal);
        Assert.Single(factory.Logs.GetSnapshot(), record => record.Message.Contains("TX-4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoreThanTheMaximum_IsTruncated_AndSaysSo()
    {
        using var small = factory.WithWebHostBuilder(builder => builder.UseSetting("Screening:StreamMaxRecords", "2"));
        small.Services.GetRequiredService<SanctionAndForgive.Api.Lists.ListCatalog>().Replace([Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity))]);
        using var client = small.CreateClient();

        var lines = await StreamAsync(client, string.Join('\n', Enumerable.Range(1, 5).Select(i => $"{{\"reference\":\"TX-{i}\",\"companyName\":\"Romashka\"}}")));

        Assert.Equal(2, lines.Count(line => line.GetProperty("type").GetString() == "result"));
        Assert.Equal(2, lines[0].GetProperty("maxRecords").GetInt32());
        Assert.True(lines[^1].GetProperty("truncated").GetBoolean());
        Assert.Equal(3, lines[^1].GetProperty("nextLine").GetInt32());
    }

    [Fact]
    public async Task PastItsTimeLimit_AStreamStops_AndSaysWhereToResume()
    {
        using var timed = factory.WithWebHostBuilder(builder => builder.UseSetting("Screening:StreamMaxSeconds", "60"));
        timed.Services.GetRequiredService<SanctionAndForgive.Api.Lists.ListCatalog>().Replace([Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity))]);
        factory.Clock.AutoAdvanceAmount = TimeSpan.FromSeconds(25);
        using var client = timed.CreateClient();

        var lines = await StreamAsync(client, string.Join('\n', Enumerable.Range(1, 20).Select(i => $"{{\"reference\":\"TX-{i}\",\"companyName\":\"Romashka\"}}")));

        var results = lines.Where(line => line.GetProperty("type").GetString() == "result").ToList();
        Assert.InRange(results.Count, 1, 19);
        Assert.Equal(60, lines[0].GetProperty("maxSeconds").GetInt32());
        Assert.True(lines[^1].GetProperty("truncated").GetBoolean());
        Assert.Equal(results.Count + 1, lines[^1].GetProperty("nextLine").GetInt32());
    }

    [Fact]
    public async Task BeforeTheFirstLoad_Is503()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("{\"reference\":\"TX-1\",\"companyName\":\"Romashka\"}", Encoding.UTF8, "application/x-ndjson");

        using var response = await client.PostAsync("/api/v1/screenings/stream", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task BoundedLineReader_HoldsAtMostTheLimit_AndSkipsTheRestOfALongLine()
    {
        using var reader = new BoundedLineReader(new StringReader("short\r\n" + new string('x', 50) + "\nnext"), maxBytes: 10);

        Assert.Equal(("short", false), await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal((string.Empty, true), await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal(("next", false), await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BoundedLineReader_CountsUtf8Bytes_NotCharacters()
    {
        // 6 characters, 12 bytes: over a 10-byte limit. 5 of them, 10 bytes: at it.
        using var reader = new BoundedLineReader(new StringReader("ЖЖЖЖЖЖ\nЖЖЖЖЖ"), maxBytes: 10);

        Assert.Equal((string.Empty, true), await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal(("ЖЖЖЖЖ", false), await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AHit_IsExplained_WithoutTheEnteredText()
    {
        Load();
        using var client = factory.CreateClient();

        var lines = await StreamAsync(client, """{"reference":"TX-1","companyName":"ТОВ Ромашка"}""");

        var best = lines[1].GetProperty("entries")[0].GetProperty("cells").EnumerateArray().Single(cell => cell.GetProperty("list").GetString() == "eu").GetProperty("best");
        var word = Assert.Single(best.GetProperty("words").EnumerateArray());
        Assert.Equal((1, "romashka", "Transliterated"), (word.GetProperty("entered").GetInt32(), word.GetProperty("listed").GetString(), word.GetProperty("how").GetString()));
        Assert.DoesNotContain("Ромашка", best.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStalledUpload_StopsAtTheTimeLimit_WithAnEndLine()
    {
        // Real Kestrel: the in-memory test server only hands the app a request once its body is complete.
        using var kestrel = new ApiFactory();
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        kestrel.Load(Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity)));
        var server = kestrel.ClientOptions.BaseAddress;

        // A raw HTTP/1.1 request with a chunked body: line 1, then half of line 2, then nothing more. (HttpClient
        // won't hand over a response while its own upload is still open.)
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port);
        await using var connection = tcp.GetStream();
        var body = "{\"reference\":\"TX-1\",\"companyName\":\"Romashka\"}\n{\"reference\":\"TX-2\",";
        var bodyBytes = Encoding.UTF8.GetByteCount(body);
        await connection.WriteAsync(Encoding.UTF8.GetBytes(
            $"POST /api/v1/screenings/stream HTTP/1.1\r\nHost: {server.Authority}\r\nContent-Type: application/x-ndjson\r\nTransfer-Encoding: chunked\r\n\r\n{bodyBytes:x}\r\n{body}\r\n"));
        await connection.FlushAsync();

        // Read the answer as it comes; once record 1 is answered, the time runs out with line 2 half sent.
        var received = new StringBuilder();
        var buffer = new byte[4096];
        var advanced = false;
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!received.ToString().Contains("\"type\":\"end\"", StringComparison.Ordinal))
        {
            var read = await connection.ReadAsync(buffer, patience.Token);
            Assert.NotEqual(0, read);
            received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (!advanced && received.ToString().Contains("\"type\":\"result\"", StringComparison.Ordinal))
            {
                advanced = true;
                kestrel.Clock.Advance(TimeSpan.FromSeconds(201));
            }
        }

        var lines = received.ToString().Split('\n').Where(line => line.TrimStart().StartsWith("{\"type\"", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line.Trim()).RootElement.Clone()).ToList();
        Assert.Equal(["header", "result", "end"], lines.Select(line => line.GetProperty("type").GetString()));
        Assert.True(lines[^1].GetProperty("truncated").GetBoolean());
        Assert.Equal(2, lines[^1].GetProperty("nextLine").GetInt32());
    }
}
