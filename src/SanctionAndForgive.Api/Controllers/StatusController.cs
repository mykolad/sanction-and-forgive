using Microsoft.AspNetCore.Mvc;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>GET /api/v1/status</c>: the index state and each list's as-of date and count. Reads memory, not SQL.
/// The website builds its rows from this, so a new list (a feed or a manual source) needs no UI change.</summary>
[ApiController]
[Route("api/v1/status")]
public sealed class StatusController(ListCatalog catalog, Screener screener, AppInfo app, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public StatusResponse Get()
    {
        var now = clock.GetUtcNow();
        return new StatusResponse(catalog.IsReady ? "ready" : "warmingUp", app.Version, screener.MatcherVersion, [.. catalog.Lists.Select(list => ListResponse.From(list, now))]);
    }
}

[ApiController]
public sealed class HealthController(ListCatalog catalog, AppInfo app) : ControllerBase
{
    /// <summary>Liveness: the process answers. No database call.</summary>
    [HttpGet("/healthz")]
    public HealthResponse Health() => new("ok", app.Version, app.Revision);

    /// <summary>Readiness: 200 once the first index load finished, even if some lists are missing (they say Not
    /// checked), so a deploy keeps the old replica serving until the new one has loaded.</summary>
    [HttpGet("/readyz")]
    public IActionResult Ready() => catalog.IsReady
        ? Ok(new HealthResponse("ready", app.Version, app.Revision))
        : StatusCode(StatusCodes.Status503ServiceUnavailable, new WarmingUpResponse("warmingUp"));
}

/// <summary>The build's version (the image's commit, <c>App:Version</c>) and the platform's revision.</summary>
public sealed record AppInfo(string Version, string? Revision)
{
    public static AppInfo From(IConfiguration configuration) => new(
        configuration["App:Version"] is { Length: > 0 } version ? version : "dev",
        Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION"));
}
