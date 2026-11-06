using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace SanctionAndForgive.Api;

/// <summary>Per client IP rate limit on screenings (<c>RateLimiting:PerMinute</c>, 120 by default, 0 = off: a load
/// test turns it off). The IP comes from <c>X-Forwarded-For</c>: only the entry the Container Apps ingress appended
/// counts (forward limit 1), so a client can't pick its own partition by sending the header.</summary>
public static class RateLimits
{
    public const string Screenings = "screenings";

    /// <summary>Streams at once, across all clients (`Screening:ConcurrentStreams`, 1): a batch takes the CPU.</summary>
    public const string Streams = "streams";

    public static IServiceCollection AddScreeningRateLimit(this IServiceCollection services, int perMinute, int concurrentStreams)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Retry-After: the window's own wait for the per-minute limit; a second for a busy stream (its permit
            // comes back as the running stream's request completes, just after its end line).
            limiter.OnRejected = (context, _) =>
            {
                var after = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 1;
                context.HttpContext.Response.Headers.RetryAfter = after.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(Screenings, context => perMinute <= 0
                ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(Streams, _ => RateLimitPartition.GetConcurrencyLimiter(
                "streams",
                _ => new ConcurrencyLimiterOptions { PermitLimit = Math.Max(1, concurrentStreams), QueueLimit = 0 }));
        });
    }
}
