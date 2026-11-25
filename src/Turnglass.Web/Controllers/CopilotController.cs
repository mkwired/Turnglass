using Microsoft.AspNetCore.Mvc;
using Turnglass.Core;

namespace Turnglass.Web.Controllers;

[ApiController]
[Route("api/copilot")]
public sealed class CopilotController(ICopilotUsageService copilot) : ControllerBase
{
    [HttpGet("usage")]
    public async Task<ActionResult<CopilotUsageReport>> GetUsageAsync(CancellationToken cancellationToken, [FromQuery] string mode = "calendar", [FromQuery] string? timezone = null, [FromQuery] string? client = null, [FromQuery] string? model = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null)
    {
        return Ok(await copilot.GetUsageAsync(new CopilotUsageQuery(mode, timezone, client, model, from, to), cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("index")]
    public async Task<ActionResult<CopilotIndexStatus>> GetIndexStatusAsync(CancellationToken cancellationToken)
    {
        return Ok(await copilot.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("filters")]
    public async Task<ActionResult<CopilotFilterOptions>> GetFiltersAsync(CancellationToken cancellationToken)
    {
        return Ok(await copilot.GetFiltersAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<CopilotSettings>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return Ok(await copilot.GetSettingsAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<CopilotSettings>> UpdateSettingsAsync([FromBody] CopilotSettings settings, CancellationToken cancellationToken)
    {
        return Ok(await copilot.UpdateSettingsAsync(settings, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("index/rescan")]
    public async Task<ActionResult<CopilotIndexStatus>> RescanAsync(CancellationToken cancellationToken)
    {
        await copilot.RescanAsync(cancellationToken).ConfigureAwait(false);
        return Ok(await copilot.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false));
    }
}
