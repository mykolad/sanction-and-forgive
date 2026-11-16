using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

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
public sealed partial class FeedDownloader(HttpClient httpClient, TimeSpan timeout, DownloadRetry retry, TimeProvider time, TextWriter log)
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

    /// <summary>The largest publisher's page read for a link (<see cref="ListSource.LinkPattern"/>).</summary>
    public const int MaxPageBytes = 4 << 20;

    /// <summary>Downloads a catalogued feed's file to a new temporary file: its URL with <paramref name="euToken"/> in
    /// place of <c>{token}</c>, or, for a feed with a <see cref="ListSource.LinkPattern"/>, the file its page links
    /// today. The page and the file are each tried again as <see cref="DownloadAsync(string, Uri, CancellationToken)"/>
    /// says.</summary>
    public async Task<string> DownloadAsync(ListSource source, string euToken, CancellationToken cancellationToken)
    {
        var url = new Uri(source.Url!.Replace("{token}", euToken, StringComparison.Ordinal));
        if (source.LinkPattern is { } pattern)
        {
            var page = await DownloadAsync(source.Code, url, MaxPageBytes, cancellationToken);
            try
            {
                url = FindLink(url, await File.ReadAllTextAsync(page, cancellationToken), pattern);
            }
            finally
            {
                File.Delete(page);
            }
        }

        return await DownloadAsync(source.Code, url, null, cancellationToken);
    }

    /// <summary>The file a page links, for a feed published under a dated name: of the page's links (<c>href</c>,
    /// entities decoded, resolved against <paramref name="page"/>) on the page's own host and scheme, those whose
    /// address matches <paramref name="linkPattern"/>; of those, the one whose matched part (the dated name) is last in
    /// ordinal order, so the newest date wherever it's filed, the whole address breaking a tie. A
    /// <see cref="ListFormatException"/> when none does: the page changed, and the import fails rather than guess.</summary>
    public static Uri FindLink(Uri page, string html, string linkPattern)
    {
        var pattern = new Regex(linkPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Uri? found = null;
        var foundKey = string.Empty;
        foreach (Match link in Href().Matches(html))
        {
            if (!Uri.TryCreate(page, WebUtility.HtmlDecode(link.Groups[1].Value.Trim()), out var target)
                || target.Scheme != page.Scheme || !string.Equals(target.Host, page.Host, StringComparison.OrdinalIgnoreCase)
                || pattern.Match(target.AbsoluteUri) is not { Success: true } match)
            {
                continue;
            }

            var order = found is null ? 1 : string.CompareOrdinal(match.Value, foundKey) is var byName and not 0 ? byName : string.CompareOrdinal(target.AbsoluteUri, found.AbsoluteUri);
            if (order > 0)
            {
                (found, foundKey) = (target, match.Value);
            }
        }

        return found ?? throw new ListFormatException($"The publisher's page has no link matching {linkPattern}.");
    }

    /// <summary>Downloads <paramref name="url"/> to a new temporary file and returns its path. Each attempt that will be
    /// tried again writes one line to the log, named by <paramref name="source"/> (never the URL: it can hold a token).
    /// Throws the last attempt's <see cref="HttpRequestException"/>, <see cref="IOException"/> or
    /// <see cref="TimeoutException"/> (one attempt longer than the timeout) once the retries are spent.</summary>
    public Task<string> DownloadAsync(string source, Uri url, CancellationToken cancellationToken) =>
        DownloadAsync(source, url, null, cancellationToken);

    /// <summary>As <see cref="DownloadAsync(string, Uri, CancellationToken)"/>, stopping as soon as the body passes
    /// <paramref name="maxBytes"/> (when given): a <see cref="ListFormatException"/>, not tried again.</summary>
    private async Task<string> DownloadAsync(string source, Uri url, long? maxBytes, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(url, maxBytes, cancellationToken);
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

                // A delay is a minimum: if it overran the budget, no attempt starts after it.
                if (time.GetElapsedTime(started) > retry.Budget)
                {
                    throw;
                }
            }
        }
    }

    private async Task<string> AttemptAsync(Uri url, long? maxBytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var path = Path.GetTempFileName();
        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            if (maxBytes is not { } limit)
            {
                await response.Content.CopyToAsync(file, deadline.Token);
                return path;
            }

            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[81920];
            var written = 0L;
            int read;
            while ((read = await body.ReadAsync(buffer, deadline.Token)) > 0)
            {
                written += read;
                if (written > limit)
                {
                    throw new ListFormatException($"The publisher's page is larger than {limit} bytes.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }

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

    [GeneratedRegex("""href\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Href();

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => string.Create(CultureInfo.InvariantCulture, $"{(int)status} {status}"),
        TimeoutException => "no complete answer in time",
        _ => exception.GetType().Name,
    };
}
