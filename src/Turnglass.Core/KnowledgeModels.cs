namespace Turnglass.Core;

public sealed record ReviewQuery(string? Project = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Cursor = null, int MaxCharacters = 24000);

public sealed record ReviewEvent(string EventId, long Ordinal, DateTimeOffset Timestamp, string Type, string? Role, string Text, string Origin, int Offset, int? NextOffset);

public sealed record ReviewCoverage(int ExaminedEvents, int ExcludedEvents, bool SourceChanged, bool IsPartial, IReadOnlyList<string> Warnings);

public sealed record ReviewPacket(string? PacketId, SessionRecord? Session, IReadOnlyList<ReviewEvent> Events, string? NextCursor, int Characters, ReviewCoverage Coverage, IndexStatus Index);

public sealed record EvidenceInput(string SessionId, string EventId, string Quote);

public sealed record InsightDraft(string Statement, string Category, string Scope, string? Project, string? SessionId, string Basis, string Rationale, IReadOnlyList<EvidenceInput> Evidence, IReadOnlyList<string>? RelatedInsightIds = null);

public sealed record ReviewSubmission(string PacketId, string IdempotencyKey, IReadOnlyList<InsightDraft> Findings);

public sealed record ReviewReceipt(string PacketId, IReadOnlyList<string> InsightIds, DateTimeOffset ReviewedAt, bool Replayed);

public sealed record InsightEvidence(string SessionId, string EventId, long Ordinal, DateTimeOffset Timestamp, string Quote, string Availability);

public sealed record InsightRevision(int Revision, string Action, DateTimeOffset Timestamp, string Statement, string? Note);

public sealed record Insight(string Id, string Statement, string Category, string Scope, string? Project, string? SessionId, string Basis, string Rationale, string Status, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<InsightEvidence> Evidence, IReadOnlyList<string> RelatedInsightIds, IReadOnlyList<InsightRevision> History);

public sealed record InsightQuery(string? Query = null, string? Project = null, string? Category = null, string? Status = null, string? Cursor = null, int Limit = 30);

public sealed record InsightPage(IReadOnlyList<Insight> Items, string? NextCursor);

public sealed record InsightAction(string Id, int ExpectedRevision, string Action, InsightDraft? Draft = null, string? TargetId = null, int? TargetRevision = null, string? Note = null);

public sealed record InsightReviewRequest(IReadOnlyList<InsightAction> Actions);

public sealed record ReviewProgress(int Drafts, int Approved, int Dismissed, int Superseded, int ReviewedSessions, long ReviewedCharacters, DateTimeOffset? LastReviewedAt);

public sealed record InsightExportRequest(IReadOnlyList<string> Ids, string Format = "markdown");

public sealed record InsightExport(string FileName, string ContentType, string Content);

public sealed record EventContext(string SessionId, string EventId, IReadOnlyList<EventView> Events, bool Truncated, IndexStatus Index);

public sealed record ReviewPrompt(string Text, int PacketCharacterLimit = 24000, int RunCharacterLimit = 120000);

public sealed class KnowledgeConflictException(string message) : InvalidOperationException(message);
