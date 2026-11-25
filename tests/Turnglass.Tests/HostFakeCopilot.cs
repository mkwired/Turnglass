using Turnglass.Core;

namespace Turnglass.Tests;

internal sealed class HostFakeCopilot : ICopilotUsageService
{
    internal CopilotSettings Settings { get; private set; } = new(false, []);
    internal CopilotUsageQuery? LastQuery { get; private set; }
    internal bool Rescanned { get; private set; }
    internal Exception? Failure { get; set; }

    public Task<CopilotSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Result(Settings);

    public Task<CopilotSettings> UpdateSettingsAsync(CopilotSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Result(settings);
    }

    public Task<CopilotIndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken = default) => Result(Status());

    public Task<CopilotUsageReport> GetUsageAsync(CopilotUsageQuery query, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        var total = new UsageTotals(new TokenUsage(100, OutputTokens: 10), 0.0015m, 0, 0, []);
        return Result(new CopilotUsageReport(query.Mode, query.Timezone ?? "UTC", HostFakeViewer.Timestamp, "fixture", [new("Today", HostFakeViewer.Timestamp.AddDays(-1), HostFakeViewer.Timestamp, total)], [new("2026-11-21", total)], ["Standard API tier assumed"], 1, 1, 1, Status()));
    }

    public Task<CopilotFilterOptions> GetFiltersAsync(CancellationToken cancellationToken = default) => Result(new CopilotFilterOptions(["shared"], ["gpt-5.4"]));

    public Task RescanAsync(CancellationToken cancellationToken = default)
    {
        Rescanned = true;
        return Result(true);
    }

    private static CopilotIndexStatus Status() => new(true, false, HostFakeViewer.Timestamp, 1, HostFakeViewer.Timestamp, HostFakeViewer.Timestamp, 0, 0, [], []);

    private Task<T> Result<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
}
