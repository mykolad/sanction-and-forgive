using System.Net.Http.Headers;

namespace SanctionAndForgive.Lists;

/// <summary>Downloads a feed to a temporary file. It identifies itself honestly (no browser user agent): a source
/// that refuses such clients is reported, not worked around.</summary>
public sealed class FeedDownloader(HttpClient httpClient)
{
    public static HttpClient CreateHttpClient(string version)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sanction-and-forgive", version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/mykolad/sanction-and-forgive)"));
        return client;
    }

    /// <summary>Downloads <paramref name="url"/> to a new temporary file and returns its path. Throws
    /// <see cref="HttpRequestException"/> on a non-success status.</summary>
    public async Task<string> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var path = Path.GetTempFileName();
        try
        {
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, cancellationToken);
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
