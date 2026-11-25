using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Turnglass.Infrastructure;

internal sealed class CopilotIndexingWorker(CopilotIndexer indexer, ILogger<CopilotIndexingWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> _scanError = LoggerMessage.Define(LogLevel.Warning, new EventId(4, nameof(CopilotIndexingWorker)), "Copilot usage scanning failed; a later refresh will retry.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await indexer.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or TimeoutException)
                {
                    _scanError(logger, exception);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Collection stops cooperatively without discarding durable usage.
        }
    }
}
