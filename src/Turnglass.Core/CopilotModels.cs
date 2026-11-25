namespace Turnglass.Core;

public sealed record CopilotSourceRoot(string Kind, string Path, bool Enabled = true);

public sealed record CopilotSettings(bool AutoDiscover, IReadOnlyList<CopilotSourceRoot> Sources);

public sealed record CopilotSourceStatus(string Kind, string Path, bool Enabled, bool Available, int FileCount, string? Error);

public sealed record CopilotIndexStatus(bool InitialScanComplete, bool IsIndexing, DateTimeOffset? LastSuccessfulScanUtc, int RecordCount, DateTimeOffset? ObservedFrom, DateTimeOffset? ObservedTo, int SkippedRecords, int AmbiguousRecords, IReadOnlyList<CopilotSourceStatus> Sources, IReadOnlyList<string> Warnings);

public sealed record CopilotUsageFact(string Id, string SessionId, DateTimeOffset Timestamp, string? Model, string Client, long InputTokens, long OutputTokens, long? CachedInputTokens = null, long? CacheWriteInputTokens = null, long? ReasoningOutputTokens = null, string? ServiceTier = null, string? CacheWriteLifetime = null);

public sealed record CopilotUsageQuery(string Mode = "calendar", string? Timezone = null, string? Client = null, string? Model = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record CopilotFilterOptions(IReadOnlyList<string> Clients, IReadOnlyList<string> Models);

public sealed record CopilotPricingResult(UsageTotals Totals, IReadOnlyList<string> Assumptions);

public sealed record CopilotUsageReport(string Mode, string Timezone, DateTimeOffset AsOf, string PricingVersion, IReadOnlyList<PeriodUsage> Periods, IReadOnlyList<UsageBucket> Buckets, IReadOnlyList<string> PricingAssumptions, int MatchingRecordCount, int UnknownCacheReadRecords, int UnknownCacheWriteRecords, CopilotIndexStatus Index);
