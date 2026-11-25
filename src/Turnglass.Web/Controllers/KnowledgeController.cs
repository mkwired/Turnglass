using Microsoft.AspNetCore.Mvc;
using Turnglass.Core;

namespace Turnglass.Web.Controllers;

[ApiController]
[Route("api")]
public sealed class KnowledgeController(IKnowledgeService knowledge) : ControllerBase
{
    [HttpGet("insights")]
    public async Task<ActionResult<InsightPage>> ListInsightsAsync(CancellationToken cancellationToken, [FromQuery] string? query = null, [FromQuery] string? project = null, [FromQuery] string? category = null, [FromQuery] string? status = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 30)
    {
        return Ok(await knowledge.ListInsightsAsync(new InsightQuery(query, project, category, status, cursor, limit), cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("insights/{id}")]
    [ProducesResponseType<Insight>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Insight>> GetInsightAsync([FromRoute] string id, CancellationToken cancellationToken)
    {
        var insight = await knowledge.GetInsightAsync(id, cancellationToken).ConfigureAwait(false);
        if (insight is null)
        {
            return NotFound();
        }

        return Ok(insight);
    }

    [HttpPost("insights/review")]
    [ProducesResponseType<IReadOnlyList<Insight>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<Insight>>> ReviewInsightsAsync([FromBody] InsightReviewRequest request, CancellationToken cancellationToken)
    {
        return Ok(await knowledge.ReviewInsightsAsync(request, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("insights/export")]
    public async Task<ActionResult<InsightExport>> ExportInsightsAsync([FromBody] InsightExportRequest request, CancellationToken cancellationToken)
    {
        return Ok(await knowledge.ExportInsightsAsync(request, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("reviews/progress")]
    public async Task<ActionResult<ReviewProgress>> GetReviewProgressAsync(CancellationToken cancellationToken)
    {
        return Ok(await knowledge.GetReviewProgressAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("reviews/prompt")]
    public ActionResult<ReviewPrompt> GetReviewPrompt([FromQuery] string? project = null)
    {
        return Ok(knowledge.GetReviewPrompt(project));
    }

    [HttpGet("sessions/{sessionId}/events/{eventId}/context")]
    [ProducesResponseType<EventContext>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventContext>> GetEventContextAsync([FromRoute] string sessionId, [FromRoute] string eventId, CancellationToken cancellationToken, [FromQuery] int before = 3, [FromQuery] int after = 3, [FromQuery] int maxCharacters = 16000)
    {
        var context = await knowledge.GetEventContextAsync(sessionId, eventId, before, after, maxCharacters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return NotFound();
        }

        return Ok(context);
    }
}
