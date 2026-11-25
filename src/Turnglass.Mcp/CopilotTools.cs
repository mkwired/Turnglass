using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Turnglass.Core;

namespace Turnglass.Mcp;

[McpServerToolType]
public sealed class CopilotTools(ICopilotUsageService copilot)
{
    [McpServerTool(Name = "get_copilot_usage", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get recorded Copilot tokens and API-equivalent USD estimates separately from Codex. Includes Visual Studio, VS Code, and shared/unattributed SDK activity (which can include CLI). Preserve coverage warnings, unknown cache counters, pricing assumptions and unpriced counts; totals are not a subscription bill or complete historical usage. client is visual-studio, vscode or shared; omit for all.")]
    public Task<CopilotUsageReport> GetCopilotUsageAsync(string mode = "calendar", string? timezone = null, string? client = null, string? model = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        return RunAsync(token => copilot.GetUsageAsync(new CopilotUsageQuery(mode, timezone, client, model, from, to), token), cancellationToken);
    }

    [McpServerTool(Name = "get_copilot_index_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Check Copilot source availability, scan freshness, recorded coverage and skipped or ambiguous records. A completed scan does not prove complete historical coverage; absent token logs must not be reported as authoritative zero usage. Retained usage survives source log rotation.")]
    public Task<CopilotIndexStatus> GetCopilotIndexStatusAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(copilot.GetIndexStatusAsync, cancellationToken);
    }

    private static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await operation(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("The Copilot index is initializing or busy. Retry get_copilot_index_status shortly.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new McpException("The Copilot index is unavailable or initializing. Check source permissions and retry get_copilot_index_status.");
        }
        catch (ArgumentException exception)
        {
            throw new McpException(exception.Message);
        }
    }
}
