using Turnglass.Core;

namespace Turnglass.Tests;

internal sealed class HostFakeViewer : IViewerService
{
    internal static DateTimeOffset Timestamp { get; } = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    internal static IndexStatus Status { get; } = new(true, false, Timestamp, 1, 1, [], []);

    internal static UsageTotals Totals { get; } = new(new TokenUsage(100, 20, 0, 10), 0.005m, 0, 0, []);

    internal static SessionView Session { get; } = new(new SessionRecord("session-a", "Synthetic session", "sample", "gpt-6-astra", null, Timestamp, Timestamp), Totals, Totals, 0);

    internal static EventView Event { get; } = new("event-a", "session-a", 1, Timestamp, "response_item", "user", "<script>alert('unsafe')</script>", false, false, false);

    internal ViewerSettings Settings { get; set; } = new([], "America/New_York", PricingDefaults.CreateCatalog());

    internal Exception? Failure { get; set; }

    internal bool Missing { get; set; }

    internal bool Rebuilt { get; private set; }

    internal SessionFilter? LastFilter { get; private set; }

    internal UsageQuery? LastUsageQuery { get; private set; }

    internal string? LastSearch { get; private set; }

    internal string? LastSessionId { get; private set; }

    internal int LastOffset { get; private set; }

    public Task<ViewerSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Result(Settings);

    public Task<ViewerSettings> UpdateSettingsAsync(ViewerSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Result(settings);
    }

    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken = default) => Result(Status);

    public Task<UsageReport> GetUsageAsync(UsageQuery query, CancellationToken cancellationToken = default)
    {
        LastUsageQuery = query;
        return Result(new UsageReport(query.Mode, query.Timezone ?? Settings.Timezone, Timestamp, Settings.Pricing.Version, [new PeriodUsage("Today", Timestamp.AddDays(-1), Timestamp, Totals)], [], Status));
    }

    public Task<PageResult<SessionView>> ListSessionsAsync(SessionFilter filter, CancellationToken cancellationToken = default)
    {
        LastFilter = filter;
        return Result(new PageResult<SessionView>([Session], null, Status));
    }

    public Task<SessionDetail?> GetSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        LastSessionId = id;
        return Result<SessionDetail?>(Missing ? null : new SessionDetail(Session, [], Status));
    }

    public Task<PageResult<EventView>> ReadEventsAsync(string sessionId, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        LastSessionId = sessionId;
        return Result(new PageResult<EventView>([Event], cursor, Status));
    }

    public Task<LogChunk?> ReadLogRecordAsync(string sessionId, string eventId, int offset = 0, int limit = 16000, CancellationToken cancellationToken = default)
    {
        LastSessionId = sessionId;
        LastOffset = offset;
        return Result<LogChunk?>(Missing ? null : new LogChunk(eventId, "{\"type\":\"response_item\"}", null, 24));
    }

    public Task<PageResult<EventView>> SearchLogsAsync(string query, string? sessionId = null, string? cursor = null, int limit = 30, CancellationToken cancellationToken = default)
    {
        LastSearch = query;
        LastSessionId = sessionId;
        return Result(new PageResult<EventView>([Event], cursor, Status));
    }

    public Task<FilterOptions> GetFiltersAsync(CancellationToken cancellationToken = default) => Result(new FilterOptions(["sample"], ["gpt-6-astra"]));

    public Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        Rebuilt = true;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    private Task<T> Result<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
}
