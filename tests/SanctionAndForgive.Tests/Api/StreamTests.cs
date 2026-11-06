using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    public async Task AListedInitial_SaysWhichEnteredWordItMatched()
    {
        factory.Load(Feed("eu", "EU", Now, new ListedSubject("X-1", SubjectKind.Individual, "Шевченко Т.Г.", [new ListedName("Шевченко Т.Г.", IsWeak: false)], [], "TEST", null)));
        using var client = factory.CreateClient();

        var lines = await StreamAsync(client, """{"reference":"TX-1","receiverName":"Taras Shevchenko"}""");

        var words = lines[1].GetProperty("entries")[0].GetProperty("cells")[0].GetProperty("best").GetProperty("words").EnumerateArray()
            .Select(word => (word.GetProperty("entered").ValueKind == JsonValueKind.Null ? (int?)null : word.GetProperty("entered").GetInt32(), word.GetProperty("listed").GetString(), word.GetProperty("how").GetString())).ToList();
        Assert.Contains((2, "шевченко", "Transliterated"), words);
        Assert.Contains((1, "т", "Initial"), words);
        Assert.Contains((null, "г", "InitialUnchecked"), words);
    }

    [Fact]
    public async Task AnAddressOnlyHit_SaysWhichListedPartsAligned_WithoutTheEnteredText()
    {
        factory.Load(Feed("eu", "EU", Now, new ListedSubject("X-2", SubjectKind.Entity, "Northwind Shipping LLC", [new ListedName("Northwind Shipping LLC", IsWeak: false)], [new ListedAddress("ул. Ленина 5, Москва", "RU")], "TEST", null)));
        using var client = factory.CreateClient();

        var lines = await StreamAsync(client, """{"reference":"TX-1","address":"Lenina 5, Moskva"}""");

        var best = lines[1].GetProperty("entries")[0].GetProperty("cells")[0].GetProperty("best");
        var how = best.GetProperty("words").EnumerateArray().Select(word => word.GetProperty("how").GetString()).ToList();
        Assert.Contains("SameNumber", how);
        Assert.True(how.Count >= 3, string.Join(", ", how));
        Assert.Contains(best.GetProperty("notes").EnumerateArray(), note => note.GetString()!.StartsWith("Address only:", StringComparison.Ordinal));
        Assert.DoesNotContain("Lenina", best.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Moskva", best.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStalledUpload_StopsAtTheTimeLimit_WithAnEndLine()
    {
        using var kestrel = new ApiFactory();

        // Line 1, then half of line 2, then nothing more; once record 1 is answered, the time runs out.
        var lines = await OpenUploadAsync(kestrel, "{\"reference\":\"TX-1\",\"companyName\":\"Romashka\"}\n{\"reference\":\"TX-2\",",
            onResult: () => kestrel.Clock.Advance(TimeSpan.FromSeconds(201)));

        Assert.Equal(["header", "result", "end"], lines.Select(line => line.GetProperty("type").GetString()));
        Assert.True(lines[^1].GetProperty("truncated").GetBoolean());
        Assert.Equal(2, lines[^1].GetProperty("nextLine").GetInt32());
    }

    [Fact]
    public async Task ExactlyTheMaximum_WithTheUploadStillOpen_EndsAtOnce()
    {
        using var parent = new ApiFactory();
        using var kestrel = parent.WithWebHostBuilder(builder => builder.UseSetting("Screening:StreamMaxRecords", "2"));

        // Two records and the upload left open: the end line comes without waiting for a third or for the deadline.
        var lines = await OpenUploadAsync(kestrel, "{\"reference\":\"TX-1\",\"companyName\":\"Romashka\"}\n{\"reference\":\"TX-2\",\"companyName\":\"Romashka\"}\n", onResult: () => { });

        Assert.Equal(["header", "result", "result", "end"], lines.Select(line => line.GetProperty("type").GetString()));
        Assert.Equal(3, lines[^1].GetProperty("nextLine").GetInt32());
    }

    /// <summary>Posts <paramref name="body"/> to the stream on real Kestrel as a raw HTTP/1.1 chunked request and
    /// leaves the upload open (HttpClient won't hand over a response while its own upload is still open, and the
    /// in-memory test server only hands the app a request once its body is complete). Reads the answer as it comes,
    /// decoding the chunks, until a complete end line has arrived; <paramref name="onResult"/> runs once, at the
    /// first result.</summary>
    private static async Task<List<JsonElement>> OpenUploadAsync(WebApplicationFactory<Program> server, string body, Action onResult)
    {
        server.UseKestrel(0);
        server.StartServer();
        server.Services.GetRequiredService<SanctionAndForgive.Api.Lists.ListCatalog>().Replace([Feed("eu", "EU", Now, Subject("Romashka LLC", SubjectKind.Entity))]);
        var address = server.ClientOptions.BaseAddress;

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port);
        await using var connection = tcp.GetStream();
        await connection.WriteAsync(Encoding.UTF8.GetBytes(
            $"POST /api/v1/screenings/stream HTTP/1.1\r\nHost: {address.Authority}\r\nContent-Type: application/x-ndjson\r\nTransfer-Encoding: chunked\r\n\r\n{Encoding.UTF8.GetByteCount(body):x}\r\n{body}\r\n"));
        await connection.FlushAsync();

        var received = new List<byte>();
        var buffer = new byte[4096];
        var resulted = false;
        List<JsonElement> lines = [];
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (lines.Count == 0 || lines[^1].GetProperty("type").GetString() != "end")
        {
            var read = await connection.ReadAsync(buffer, patience.Token);
            Assert.NotEqual(0, read);
            received.AddRange(buffer.AsSpan(0, read));
            lines = CompleteLines(DechunkedBody([.. received]));
            if (!resulted && lines.Any(line => line.GetProperty("type").GetString() == "result"))
            {
                resulted = true;
                onResult();
            }
        }

        return lines;
    }

    /// <summary>The body of a chunked HTTP/1.1 response received so far: the complete chunks after the headers.</summary>
    private static byte[] DechunkedBody(byte[] response)
    {
        var start = response.AsSpan().IndexOf("\r\n\r\n"u8);
        if (start < 0)
        {
            return [];
        }

        var body = new List<byte>();
        var position = start + 4;
        while (true)
        {
            var sizeEnd = response.AsSpan(position).IndexOf("\r\n"u8);
            if (sizeEnd < 0)
            {
                return [.. body];
            }

            var size = Convert.ToInt32(Encoding.ASCII.GetString(response, position, sizeEnd), 16);
            var dataStart = position + sizeEnd + 2;
            if (size == 0 || dataStart + size + 2 > response.Length)
            {
                return [.. body];
            }

            body.AddRange(response.AsSpan(dataStart, size));
            position = dataStart + size + 2;
        }
    }

    /// <summary>The NDJSON lines that have arrived whole (ended by a line break).</summary>
    private static List<JsonElement> CompleteLines(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var end = text.LastIndexOf('\n');
        return end < 0 ? [] : [.. text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }
}
