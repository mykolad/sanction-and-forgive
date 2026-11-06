using System.Net.Http.Headers;

namespace SanctionAndForgive.Lists;

/// <summary>Downloads a feed to a temporary file. It identifies itself honestly (no browser user agent): a source
/// that refuses such clients is reported, not worked around.</summary>
public sealed class FeedDownloader(HttpClient httpClient, TimeSpan timeout)
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

    /// <summary>Downloads <paramref name="url"/> to a new temporary file and returns its path. Throws
    /// <see cref="HttpRequestException"/> on a non-success status, and <see cref="TimeoutException"/> if the whole
    /// download takes longer than the timeout.</summary>
    public async Task<string> DownloadAsync(Uri url, CancellationToken cancellationToken)
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
}
