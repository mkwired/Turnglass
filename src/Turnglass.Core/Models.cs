namespace Turnglass.Core;

public sealed record TokenUsage(long InputTokens = 0, long CachedInputTokens = 0, long CacheWriteInputTokens = 0, long OutputTokens = 0, long ReasoningOutputTokens = 0)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record UsageFact(string Id, string SessionId, DateTimeOffset Timestamp, string? Model, string? ServiceTier, TokenUsage Tokens, bool ContextKnown = true, string? CacheWriteLifetime = null);

public sealed record PriceRate(string Model, string Tier, long MinInputTokens, long? MaxInputTokens, decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion, decimal? CacheWrite30mPerMillion = null, decimal? CacheWrite12hPerMillion = null, decimal? CacheWrite5mPerMillion = null, decimal? CacheWrite1hPerMillion = null);

public sealed record PricingCatalog(string Version, string SourceUrl, IReadOnlyList<PriceRate> Rates, IReadOnlyDictionary<string, string> ModelAliases, IReadOnlyDictionary<string, string> TierOverrides, IReadOnlyDictionary<string, string> CacheWriteLifetimeOverrides);

public sealed record ViewerSettings(IReadOnlyList<string> Sources, string Timezone, PricingCatalog Pricing);

public sealed record UsageTotals(TokenUsage Tokens, decimal KnownCostUsd, long UnpricedTokens, int UnpricedEvents, IReadOnlyList<string> PricingWarnings)
{
    public bool IsPricingComplete => UnpricedEvents == 0;
}

public sealed record PeriodUsage(string Label, DateTimeOffset Start, DateTimeOffset End, UsageTotals Totals);

public sealed record UsageBucket(string Date, UsageTotals Totals);

public sealed record SourceStatus(string Path, bool Available, int FileCount, string? Error);

public sealed record IndexStatus(bool InitialIndexComplete, bool IsIndexing, DateTimeOffset? LastSuccessfulScanUtc, int SessionCount, int EventCount, IReadOnlyList<SourceStatus> Sources, IReadOnlyList<string> Warnings)
{
    public bool IsUsageComplete => InitialIndexComplete && Warnings.Count == 0;
}

public sealed record UsageReport(string Mode, string Timezone, DateTimeOffset AsOf, string PricingVersion, IReadOnlyList<PeriodUsage> Periods, IReadOnlyList<UsageBucket> Buckets, IndexStatus Index);

public sealed record SessionRecord(string Id, string Title, string? Project, string? Model, string? ParentId, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt);

public sealed record SessionView(SessionRecord Session, UsageTotals OwnUsage, UsageTotals FamilyUsage, int ChildCount);

public sealed record LogEvent(string Id, string SessionId, long Ordinal, DateTimeOffset Timestamp, string Type, string? Role, string Text, string Raw, bool Inherited = false, bool Encrypted = false);

public sealed record EventView(string Id, string SessionId, long Ordinal, DateTimeOffset Timestamp, string Type, string? Role, string Text, bool Inherited, bool Encrypted, bool Truncated);

public sealed record LogChunk(string EventId, string Text, int? NextOffset, int TotalLength);

public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor, IndexStatus Index);

public sealed record SessionDetail(SessionView Detail, IReadOnlyList<SessionView> Children, IndexStatus Index);

public sealed record SessionFilter(string? Query = null, string? Project = null, string? Model = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Cursor = null, int Limit = 30);

public sealed record UsageQuery(string Mode = "calendar", string? Timezone = null, string? Project = null, string? Model = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record FilterOptions(IReadOnlyList<string> Projects, IReadOnlyList<string> Models);
