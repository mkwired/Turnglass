namespace Turnglass.Core;

public interface ICopilotUsageService
{
    Task<CopilotSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<CopilotSettings> UpdateSettingsAsync(CopilotSettings settings, CancellationToken cancellationToken = default);

    Task<CopilotIndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken = default);

    Task<CopilotUsageReport> GetUsageAsync(CopilotUsageQuery query, CancellationToken cancellationToken = default);

    Task<CopilotFilterOptions> GetFiltersAsync(CancellationToken cancellationToken = default);

    Task RescanAsync(CancellationToken cancellationToken = default);
}
