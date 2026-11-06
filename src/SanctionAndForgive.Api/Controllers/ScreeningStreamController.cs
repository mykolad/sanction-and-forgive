using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SanctionAndForgive.Api.Screening;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>POST /api/v1/screenings/stream</c>: many screenings in one request, without storing any of them.
/// The request is NDJSON, one <see cref="ScreeningRequestBody"/> per line; the response is NDJSON too: a header, one
/// result (or error) line per record as soon as it's screened, then an end line. Only the line in hand is held, so
/// memory stays flat however long the stream; each record writes its own audit line, never its input. One stream
/// at a time (<c>Screening:ConcurrentStreams</c>), so a batch can't starve the website on one small replica.
/// A stream stops after <c>Screening:StreamMaxRecords</c> records or <c>Screening:StreamMaxSeconds</c> seconds (the
/// ingress ends any request at 240), whichever comes first; the end line then says which line to resume from. The
/// time limit also bounds reading, so a slow or stalled upload can't hold the one stream past it.</summary>
[ApiController]
[Route("api/v1/screenings/stream")]
public sealed class ScreeningStreamController(ScreeningService screenings, IConfiguration configuration, TimeProvider clock) : ControllerBase
{
    /// <summary>The longest line read, in UTF-8 bytes; longer ones are an error line (and the rest of that line is
    /// skipped).</summary>
    public const int MaxLineBytes = 4096;

    /// <summary>After the time limit, how long writing the last lines may take before the stream is dropped.</summary>
    private static readonly TimeSpan WriteGrace = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    [HttpPost]
    [EnableRateLimiting(RateLimits.Streams)]
    [Consumes("application/x-ndjson", "application/jsonl", "text/plain")]
    [Produces("application/x-ndjson")]
    [DisableRequestSizeLimit]
    public async Task Stream(CancellationToken cancellationToken)
    {
        if (!screenings.IsReady)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsJsonAsync(new WarmingUpResponse("warmingUp"), cancellationToken);
            return;
        }

        var maxRecords = configuration.GetValue("Screening:StreamMaxRecords", 50_000);
        var maxSeconds = configuration.GetValue("Screening:StreamMaxSeconds", 200);
        var deadline = clock.GetUtcNow().AddSeconds(maxSeconds);

        // Reading stops at the deadline (the end line says where to resume); writing gets a short grace after it, so
        // the end line can still go out, but a client that stops reading can't hold the stream either.
        using var timeUp = new CancellationTokenSource(TimeSpan.FromSeconds(maxSeconds), clock);
        using var dropAt = new CancellationTokenSource(TimeSpan.FromSeconds(maxSeconds) + WriteGrace, clock);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeUp.Token);
        using var writing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dropAt.Token);
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "application/x-ndjson; charset=utf-8";

        // One snapshot for the whole stream: every result in it has the versions the header states.
        var lists = screenings.Snapshot;
        var now = screenings.Now;
        await WriteLineAsync(new StreamHeaderLine("header", screenings.MatcherVersion, ScreeningResponse.DisclaimerText, [.. lists.Select(list => ListResponse.From(list, now))], maxRecords, maxSeconds), writing.Token);

        using var reader = new BoundedLineReader(new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024), MaxLineBytes);
        int line = 0, screened = 0, errors = 0;
        int? nextLine = null;
        while (true)
        {
            (string Value, bool TooLong)? read;
            try
            {
                // WaitAsync as well: a body stream that ignores the token still can't hold the stream past the limit.
                read = await reader.ReadLineAsync(reading.Token).WaitAsync(reading.Token);
            }
            catch (OperationCanceledException) when (timeUp.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Out of time mid-line: that line wasn't read.
                nextLine = line + 1;
                break;
            }

            if (read is not { } text)
            {
                break;
            }

            line++;
            if (timeUp.IsCancellationRequested || clock.GetUtcNow() >= deadline)
            {
                nextLine = line;
                break;
            }

            if (text.Value.Length == 0 && !text.TooLong)
            {
                continue;
            }

            if (screened + errors >= maxRecords)
            {
                nextLine = line;
                break;
            }

            var (body, problem) = Parse(text);
            if (problem is not null)
            {
                errors++;
                await WriteLineAsync(new StreamErrorLine("error", line, body?.Reference is { Length: <= 64 } reference ? reference : null, problem), writing.Token);
                continue;
            }

            var result = screenings.ScreenForStream(body!, lists);
            screened++;
            await WriteLineAsync(new StreamResultLine("result", line, body!.Reference!, result.Outcome.ToString(), [.. result.Entries.Select(entry => StreamEntry.From(entry, screenings.Options))]), writing.Token);
        }

        await WriteLineAsync(new StreamEndLine("end", screened, errors, nextLine is not null, nextLine), writing.Token);
    }

    private static (ScreeningRequestBody? Body, Dictionary<string, string[]>? Problem) Parse((string Value, bool TooLong) text)
    {
        if (text.TooLong)
        {
            return (null, new Dictionary<string, string[]> { [string.Empty] = [$"The line is longer than {MaxLineBytes} bytes."] });
        }

        ScreeningRequestBody? body;
        try
        {
            body = JsonSerializer.Deserialize<ScreeningRequestBody>(text.Value, Json);
        }
        catch (JsonException)
        {
            // The parser's message can quote the input: say only that it isn't valid.
            return (null, new Dictionary<string, string[]> { [string.Empty] = ["The line isn't a valid JSON object."] });
        }

        if (body is null)
        {
            return (null, new Dictionary<string, string[]> { [string.Empty] = ["The line isn't a JSON object."] });
        }

        var errors = ScreeningService.Validate(body);
        return errors.Count > 0 ? (body, errors) : (body, null);
    }

    private async Task WriteLineAsync<T>(T line, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(Response.Body, line, Json, cancellationToken);
        await Response.Body.WriteAsync(NewLine, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
