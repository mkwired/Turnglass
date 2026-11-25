namespace Turnglass.Core;

public interface IKnowledgeService
{
    Task<ReviewPacket> GetReviewPacketAsync(ReviewQuery query, CancellationToken cancellationToken = default);

    Task<EventContext?> GetEventContextAsync(string sessionId, string eventId, int before = 3, int after = 3, int maxCharacters = 16000, CancellationToken cancellationToken = default);

    Task<ReviewReceipt> SubmitReviewAsync(ReviewSubmission submission, CancellationToken cancellationToken = default);

    Task<InsightPage> ListInsightsAsync(InsightQuery query, bool approvedOnly = false, CancellationToken cancellationToken = default);

    Task<Insight?> GetInsightAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Insight>> ReviewInsightsAsync(InsightReviewRequest request, CancellationToken cancellationToken = default);

    Task<ReviewProgress> GetReviewProgressAsync(CancellationToken cancellationToken = default);

    Task<InsightExport> ExportInsightsAsync(InsightExportRequest request, CancellationToken cancellationToken = default);

    ReviewPrompt GetReviewPrompt(string? project = null);
}
