using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace SanctionAndForgive.Api;

/// <summary>Per client IP rate limit on screenings (<c>RateLimiting:PerMinute</c>, 120 by default, 0 = off: a load
/// test turns it off). The IP comes from <c>X-Forwarded-For</c>: only the entry the Container Apps ingress appended
/// counts (forward limit 1), so a client can't pick its own partition by sending the header.</summary>
public static class RateLimits
{
    public const string Screenings = "screenings";

    public static IServiceCollection AddScreeningRateLimit(this IServiceCollection services, int perMinute)
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
            limiter.AddPolicy(Screenings, context => perMinute <= 0
                ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
