using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [HttpGet("live")]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "healthy",
            timestampUtc = DateTime.UtcNow
        });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready([FromServices] IReadinessProbe probe, CancellationToken cancellationToken)
    {
        Response?.Headers.TryAdd("Cache-Control", "no-store");
        var ready = await probe.IsReadyAsync(cancellationToken);
        return StatusCode(ready ? 200 : 503, new { status = ready ? "ready" : "not_ready", timestampUtc = DateTime.UtcNow });
    }
}
