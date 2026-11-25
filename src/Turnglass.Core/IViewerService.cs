namespace Turnglass.Core;

public interface IViewerService
{
    Task<ViewerSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<ViewerSettings> UpdateSettingsAsync(ViewerSettings settings, CancellationToken cancellationToken = default);

    Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken = default);

    Task<UsageReport> GetUsageAsync(UsageQuery query, CancellationToken cancellationToken = default);

    Task<PageResult<SessionView>> ListSessionsAsync(SessionFilter filter, CancellationToken cancellationToken = default);

    Task<SessionDetail?> GetSessionAsync(string id, CancellationToken cancellationToken = default);

    Task<PageResult<EventView>> ReadEventsAsync(string sessionId, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<LogChunk?> ReadLogRecordAsync(string sessionId, string eventId, int offset = 0, int limit = 16000, CancellationToken cancellationToken = default);

    Task<PageResult<EventView>> SearchLogsAsync(string query, string? sessionId = null, string? cursor = null, int limit = 30, CancellationToken cancellationToken = default);

    Task<FilterOptions> GetFiltersAsync(CancellationToken cancellationToken = default);

    Task RebuildAsync(CancellationToken cancellationToken = default);
}
