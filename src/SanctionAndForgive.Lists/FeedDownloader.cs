using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace SanctionAndForgive.Lists;

/// <summary>How a download that hit a passing fault is tried again: after each of <paramref name="Delays"/> in turn
/// (exponential backoff), but never starting an attempt more than <paramref name="Budget"/> after the first one.</summary>
public sealed record DownloadRetry(IReadOnlyList<TimeSpan> Delays, TimeSpan Budget)
{
    /// <summary>Five retries, 15 s doubling to 4 minutes (about 8 minutes of waiting), within 10 minutes of the first
    /// attempt (owner's decision, 2026-11-09, after the EU's server answered 500 at 03:30 UTC two nights running).</summary>
    public static readonly DownloadRetry Default = new(
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4)],
        TimeSpan.FromMinutes(10));

    public static readonly DownloadRetry None = new([], TimeSpan.Zero);
}

/// <summary>Downloads a feed to a temporary file. It identifies itself honestly (no browser user agent): a source
/// that refuses such clients is reported, not worked around. A passing fault (a server error, 408, 429, a broken
/// connection, an attempt that timed out) is tried again as <see cref="DownloadRetry"/> says; anything else (404, 403)
/// fails at once.</summary>
public sealed class FeedDownloader(HttpClient httpClient, TimeSpan timeout, DownloadRetry retry, TimeProvider time, TextWriter log)
{
    /// <summary>An HTTP client without a timeout of its own: <see cref="DownloadAsync"/> applies one to the whole
    /// download, body included (<c>HttpClient.Timeout</c> stops at the headers with <c>ResponseHeadersRead</c>).</summary>
    public static HttpClient CreateHttpClient(string version)
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sanction-and-forgive", version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/mykolad/sanction-and-forgive)"));
        return client;
    }

    /// <summary>Downloads <paramref name="url"/> to a new temporary file and returns its path. Each attempt that will be
    /// tried again writes one line to the log, named by <paramref name="source"/> (never the URL: it can hold a token).
    /// Throws the last attempt's <see cref="HttpRequestException"/>, <see cref="IOException"/> or
    /// <see cref="TimeoutException"/> (one attempt longer than the timeout) once the retries are spent.</summary>
    public async Task<string> DownloadAsync(string source, Uri url, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(url, cancellationToken);
            }
            catch (Exception exception) when (IsPassing(exception) && attempt <= retry.Delays.Count)
            {
                var delay = retry.Delays[attempt - 1];
                if (time.GetElapsedTime(started) + delay > retry.Budget)
                {
                    throw;
                }

                // Worded without "failed" or "rejected": the import alert looks for those (infra/platform.bicep), and
                // only the final outcome should raise it.
                await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{source}: download attempt {attempt} of {retry.Delays.Count + 1} got {Describe(exception)}; trying again in {delay.TotalSeconds:N0} s."));
                await Task.Delay(delay, time, cancellationToken);
            }
        }
    }

    private async Task<string> AttemptAsync(Uri url, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var path = Path.GetTempFileName();
        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, deadline.Token);
            return path;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            File.Delete(path);
            throw new TimeoutException($"The download took longer than {timeout.TotalMinutes:N0} minutes.");
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>A fault that may pass: the server's (5xx), a timeout or throttling (408, 429), or the connection's
    /// (no status at all, or the body cut off). A refusal (403, 404) won't change by asking again.</summary>
    private static bool IsPassing(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        TimeoutException or IOException => true,
        _ => false,
    };

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => string.Create(CultureInfo.InvariantCulture, $"{(int)status} {status}"),
        TimeoutException => "no complete answer in time",
        _ => exception.GetType().Name,
    };
}
