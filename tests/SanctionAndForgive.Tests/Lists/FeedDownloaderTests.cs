using System.Net;
using SanctionAndForgive.Lists;

namespace SanctionAndForgive.Tests.Lists;

// The downloader's retries, with DownloadRetry.Default, a fake server and a clock that jumps instead of waiting.
public sealed class FeedDownloaderTests : IDisposable
{
    private static readonly Uri Url = new("https://feeds.example/list.xml?token=secret");

    private readonly InstantTime time = new();
    private readonly StringWriter log = new();

    public void Dispose() => log.Dispose();

    private FeedDownloader Downloader(FakeServer server) =>
        new(new HttpClient(server), TimeSpan.FromMinutes(10), DownloadRetry.Default, time, log);

    [Fact]
    public async Task AServerError_IsTriedAgain_WithBackoff_UntilItAnswers()
    {
        var server = new FakeServer(time, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var path = await Downloader(server).DownloadAsync("eu", Url, CancellationToken.None);

        try
        {
            Assert.Equal("<list />", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(3, server.Requests);
        Assert.Equal([TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)], time.Waits);
        var lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["eu: download attempt 1 of 6 got 500 InternalServerError; trying again in 15 s.", "eu: download attempt 2 of 6 got 503 ServiceUnavailable; trying again in 30 s."],
            lines);
        // The import alert looks for these words; a retry that then succeeds mustn't raise it. The URL holds a token.
        Assert.DoesNotContain("failed", log.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rejected", log.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatKeepsFailing_GetsFiveRetries_ThenTheLastErrorIsThrown()
    {
        var server = new FakeServer(time, [.. Enumerable.Repeat<HttpStatusCode?>(HttpStatusCode.BadGateway, 10)]);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Downloader(server).DownloadAsync("eu", Url, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(6, server.Requests);
        Assert.Equal([15, 30, 60, 120, 240], time.Waits.Select(wait => wait.TotalSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusal_IsNotTriedAgain(HttpStatusCode status)
    {
        var server = new FakeServer(time, status, HttpStatusCode.OK);

        await Assert.ThrowsAsync<HttpRequestException>(() => Downloader(server).DownloadAsync("eu", Url, CancellationToken.None));

        Assert.Equal(1, server.Requests);
        Assert.Empty(time.Waits);
    }

    [Fact]
    public async Task ThrottlingAndABrokenConnection_AreTriedAgain()
    {
        var server = new FakeServer(time, HttpStatusCode.TooManyRequests, null, HttpStatusCode.RequestTimeout, HttpStatusCode.OK);

        File.Delete(await Downloader(server).DownloadAsync("eu", Url, CancellationToken.None));

        Assert.Equal(4, server.Requests);
    }

    [Fact]
    public async Task NoAttemptStarts_MoreThanTenMinutesAfterTheFirst()
    {
        // Each attempt takes 3 minutes: the third ends at 9:45, and a 1-minute wait would start the fourth at 10:45.
        var server = new FakeServer(time, [.. Enumerable.Repeat<HttpStatusCode?>(HttpStatusCode.InternalServerError, 10)]) { Takes = TimeSpan.FromMinutes(3) };

        await Assert.ThrowsAsync<HttpRequestException>(() => Downloader(server).DownloadAsync("eu", Url, CancellationToken.None));

        Assert.Equal(3, server.Requests);
    }

    [Fact]
    public async Task AWaitThatOverrunsTheBudget_StartsNoFurtherAttempt()
    {
        // Each attempt takes 3 minutes. The second wait, 30 s from 6:15, is allowed (6:45), but it overruns by 4 minutes
        // to 10:45: past the budget, so the third attempt never starts.
        time.Overrun = [TimeSpan.Zero, TimeSpan.FromMinutes(4)];
        var server = new FakeServer(time, [.. Enumerable.Repeat<HttpStatusCode?>(HttpStatusCode.InternalServerError, 10)]) { Takes = TimeSpan.FromMinutes(3) };

        await Assert.ThrowsAsync<HttpRequestException>(() => Downloader(server).DownloadAsync("eu", Url, CancellationToken.None));

        Assert.Equal(2, server.Requests);
    }

    /// <summary>Answers each request with the next status (null: the connection breaks), "&lt;list /&gt;" on 200.</summary>
    private sealed class FakeServer(InstantTime time, params HttpStatusCode?[] answers) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public TimeSpan Takes { get; init; } = TimeSpan.Zero;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var answer = answers[Requests++];
            time.Pass(Takes);
            return answer is { } status
                ? Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK ? "<list />" : "error") })
                : throw new HttpRequestException("The connection was reset.");
        }
    }

    /// <summary>A clock whose timers fire at once, moving the time on by what they waited.</summary>
    private sealed class InstantTime : TimeProvider
    {
        private long ticks;

        public List<TimeSpan> Waits { get; } = [];

        /// <summary>How much longer than asked each wait takes, in turn (a timer only promises a minimum).</summary>
        public List<TimeSpan> Overrun { get; set; } = [];

        public void Pass(TimeSpan span) => ticks += span.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Waits.Add(dueTime);
            Pass(dueTime + (Waits.Count <= Overrun.Count ? Overrun[Waits.Count - 1] : TimeSpan.Zero));
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new Fired();
        }

        private sealed class Fired : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
