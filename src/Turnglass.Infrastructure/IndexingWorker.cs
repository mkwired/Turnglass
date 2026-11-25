using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Turnglass.Infrastructure;

internal sealed class IndexingWorker(SessionIndexer indexer, IHostApplicationLifetime lifetime, ViewerDatabase database, ILogger<IndexingWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> _indexError = LoggerMessage.Define(LogLevel.Error, new EventId(2, nameof(IndexingWorker)), "Session indexing failed; a later refresh will retry.");
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly List<FileSystemWatcher> _watchers = [];
    private string _sourceKey = string.Empty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before schema work so MCP initialization and discovery never await a history scan.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await indexer.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    await UpdateWatchersAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or TimeoutException)
                {
                    _indexError(logger, exception);
                }

                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                delayCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await _changes.Reader.ReadAsync(delayCancellation.Token).ConfigureAwait(false);
                    await Task.Delay(200, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Reconciliation also detects renames, missed notifications, and settings changes.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Both the web host and stdio host shut down cooperatively.
        }
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        base.Dispose();
    }

    private async Task UpdateWatchersAsync(CancellationToken cancellationToken)
    {
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var settings = await ViewerDatabase.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var available = settings.Sources.Where(Directory.Exists).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var key = string.Join('|', available);
        if (key == _sourceKey)
        {
            return;
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        foreach (var source in available)
        {
            var watcher = new FileSystemWatcher(source, "*.jsonl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        _sourceKey = key;
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => _changes.Writer.TryWrite(true);

    private void OnError(object sender, ErrorEventArgs args) => _changes.Writer.TryWrite(true);
}
