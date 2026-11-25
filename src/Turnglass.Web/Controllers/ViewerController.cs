using Microsoft.AspNetCore.Mvc;
using Turnglass.Core;

namespace Turnglass.Web.Controllers;

[ApiController]
[Route("api")]
public sealed class ViewerController(IViewerService viewer) : ControllerBase
{
    [HttpGet("usage")]
    public async Task<ActionResult<UsageReport>> GetUsageAsync(CancellationToken cancellationToken, [FromQuery] string mode = "calendar", [FromQuery] string? timezone = null, [FromQuery] string? project = null, [FromQuery] string? model = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null)
    {
        return Ok(await viewer.GetUsageAsync(new UsageQuery(mode, timezone, project, model, from, to), cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<PageResult<SessionView>>> ListSessionsAsync(CancellationToken cancellationToken, [FromQuery] string? query = null, [FromQuery] string? project = null, [FromQuery] string? model = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 30)
    {
        return Ok(await viewer.ListSessionsAsync(new SessionFilter(query, project, model, from, to, cursor, limit), cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("sessions/{id}")]
    [ProducesResponseType<SessionDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SessionDetail>> GetSessionAsync([FromRoute] string id, CancellationToken cancellationToken)
    {
        var session = await viewer.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return NotFound();
        }

        return Ok(session);
    }

    [HttpGet("sessions/{id}/events")]
    public async Task<ActionResult<PageResult<EventView>>> ReadEventsAsync([FromRoute] string id, CancellationToken cancellationToken, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        return Ok(await viewer.ReadEventsAsync(id, cursor, limit, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("sessions/{id}/records/{eventId}")]
    [ProducesResponseType<LogChunk>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LogChunk>> ReadLogRecordAsync([FromRoute] string id, [FromRoute] string eventId, CancellationToken cancellationToken, [FromQuery] int offset = 0, [FromQuery] int limit = 16000)
    {
        var chunk = await viewer.ReadLogRecordAsync(id, eventId, offset, limit, cancellationToken).ConfigureAwait(false);
        if (chunk is null)
        {
            return NotFound();
        }

        return Ok(chunk);
    }

    [HttpGet("search")]
    public async Task<ActionResult<PageResult<EventView>>> SearchLogsAsync([FromQuery] string query, CancellationToken cancellationToken, [FromQuery] string? sessionId = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 30)
    {
        return Ok(await viewer.SearchLogsAsync(query, sessionId, cursor, limit, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("filters")]
    public async Task<ActionResult<FilterOptions>> GetFiltersAsync(CancellationToken cancellationToken)
    {
        return Ok(await viewer.GetFiltersAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("index")]
    public async Task<ActionResult<IndexStatus>> GetIndexStatusAsync(CancellationToken cancellationToken)
    {
        return Ok(await viewer.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<ViewerSettings>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return Ok(await viewer.GetSettingsAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ViewerSettings>> UpdateSettingsAsync([FromBody] ViewerSettings settings, CancellationToken cancellationToken)
    {
        return Ok(await viewer.UpdateSettingsAsync(settings, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("index/rebuild")]
    public async Task<ActionResult<IndexStatus>> RebuildIndexAsync(CancellationToken cancellationToken)
    {
        await viewer.RebuildAsync(cancellationToken).ConfigureAwait(false);
        return Ok(await viewer.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false));
    }
}
