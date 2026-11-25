using Turnglass.Core;

namespace Turnglass.Tests;

internal sealed class HostFakeKnowledge : IKnowledgeService
{
    internal static Insight Sample { get; } = new("insight-a", "Use narrow tests", "preference", "project", "sample", null, "explicit", "The user requested this.", "draft", 1, HostFakeViewer.Timestamp, HostFakeViewer.Timestamp, [new("session-a", "event-a", 1, HostFakeViewer.Timestamp, "Use narrow tests", "available")], [], []);

    internal Exception? Failure { get; set; }
    internal bool Missing { get; set; }
    internal InsightQuery? LastQuery { get; private set; }
    internal bool ApprovedOnly { get; private set; }
    internal ReviewQuery? LastReviewQuery { get; private set; }
    internal ReviewSubmission? LastSubmission { get; private set; }
    internal InsightReviewRequest? LastReview { get; private set; }
    internal InsightExportRequest? LastExport { get; private set; }
    internal string? LastProject { get; private set; }
    internal (string SessionId, string EventId, int Before, int After, int MaxCharacters)? LastContext { get; private set; }

    public Task<ReviewPacket> GetReviewPacketAsync(ReviewQuery query, CancellationToken cancellationToken = default)
    {
        LastReviewQuery = query;
        return Result(new ReviewPacket("packet-a", HostFakeViewer.Session.Session, [new("event-a", 1, HostFakeViewer.Timestamp, "response_item", "user", "Use narrow tests", "session", 0, null)], "next", 16, new(1, 0, false, false, []), HostFakeViewer.Status));
    }

    public Task<EventContext?> GetEventContextAsync(string sessionId, string eventId, int before = 3, int after = 3, int maxCharacters = 16000, CancellationToken cancellationToken = default)
    {
        LastContext = (sessionId, eventId, before, after, maxCharacters);
        return Result<EventContext?>(Missing ? null : new(sessionId, eventId, [HostFakeViewer.Event], false, HostFakeViewer.Status));
    }

    public Task<ReviewReceipt> SubmitReviewAsync(ReviewSubmission submission, CancellationToken cancellationToken = default)
    {
        LastSubmission = submission;
        return Result(new ReviewReceipt(submission.PacketId, submission.Findings.Count == 0 ? [] : [Sample.Id], HostFakeViewer.Timestamp, false));
    }

    public Task<InsightPage> ListInsightsAsync(InsightQuery query, bool approvedOnly = false, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        ApprovedOnly = approvedOnly;
        return Result(new InsightPage([Sample with { Status = approvedOnly ? "approved" : "draft" }], "next"));
    }

    public Task<Insight?> GetInsightAsync(string id, CancellationToken cancellationToken = default) => Result<Insight?>(Missing ? null : Sample with { Id = id });

    public Task<IReadOnlyList<Insight>> ReviewInsightsAsync(InsightReviewRequest request, CancellationToken cancellationToken = default)
    {
        LastReview = request;
        return Result<IReadOnlyList<Insight>>([Sample with { Revision = 2, Status = "approved" }]);
    }

    public Task<ReviewProgress> GetReviewProgressAsync(CancellationToken cancellationToken = default) => Result(new ReviewProgress(1, 2, 3, 4, 5, 1000, HostFakeViewer.Timestamp));

    public Task<InsightExport> ExportInsightsAsync(InsightExportRequest request, CancellationToken cancellationToken = default)
    {
        LastExport = request;
        return Result(new InsightExport("insights.md", "text/markdown", "# Approved insights\n"));
    }

    public ReviewPrompt GetReviewPrompt(string? project = null)
    {
        LastProject = project;
        if (Failure is not null)
        {
            throw Failure;
        }

        return new("Review local history for " + project);
    }

    private Task<T> Result<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
}
