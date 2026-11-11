using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SanctionAndForgive.Api.Screening;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>POST /api/v1/screenings</c>: one screening. Reads memory only: a screening never touches the
/// database. The input is never stored or logged; it comes back only in this response, and the logs hold the
/// caller's reference with what each list answered.</summary>
[ApiController]
[Route("api/v1/screenings")]
public sealed class ScreeningsController(ScreeningService screenings, ScreeningGate gate) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(RateLimits.Screenings)]
    [ProducesResponseType<ScreeningResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<WarmingUpResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Screen(ScreeningRequestBody body)
    {
        var errors = ScreeningService.Validate(body);
        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        if (!screenings.IsReady)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new WarmingUpResponse("warmingUp"));
        }

        var response = await gate.TryRunAsync(() => screenings.Screen(body), HttpContext.RequestAborted);
        if (response is null)
        {
            // As many screenings running and waiting as allowed (ScreeningGate): try again in a second.
            Response.Headers.RetryAfter = "1";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new WarmingUpResponse("busy"));
        }

        return Ok(response);
    }
}
