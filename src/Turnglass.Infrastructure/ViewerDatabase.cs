using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public sealed class ViewerDatabase(ViewerOptions options) : IDisposable
{
    private readonly SemaphoreSlim _initialization = new(1);
    private bool _initialized;

    internal string DataDirectory { get; } = Path.GetFullPath(options.DataDirectory);

    public void Dispose() => _initialization.Dispose();

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    internal FileStream? TryAcquireLock()
    {
        Directory.CreateDirectory(DataDirectory);
        try
        {
            return new FileStream(Path.Combine(DataDirectory, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33 or 11)
        {
            return null;
        }
    }

    internal async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = TryAcquireLock();
            if (acquired is not null)
            {
                return acquired;
            }

            if (timer.Elapsed >= TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The index is busy. Retry after the current initialization or indexing pass finishes.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
            using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
            using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version > 1)
            {
                throw new InvalidOperationException("This index was created by a newer version. Restart both viewer hosts after upgrading.");
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS configuration (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS files (path TEXT PRIMARY KEY COLLATE NOCASE, source TEXT NOT NULL, offset INTEGER NOT NULL, prefix_length INTEGER NOT NULL, prefix_hash TEXT NOT NULL, state TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events (id TEXT NOT NULL, path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE, session_id TEXT NOT NULL, ordinal INTEGER NOT NULL, timestamp TEXT NOT NULL, kind TEXT NOT NULL, role TEXT, text TEXT NOT NULL, raw TEXT NOT NULL, inherited INTEGER NOT NULL, encrypted INTEGER NOT NULL, PRIMARY KEY(path,id));
                CREATE INDEX IF NOT EXISTS ix_events_session ON events(session_id,ordinal);
                CREATE VIRTUAL TABLE IF NOT EXISTS event_search USING fts5(text, content='events', content_rowid='rowid');
                CREATE TRIGGER IF NOT EXISTS events_ai AFTER INSERT ON events BEGIN INSERT INTO event_search(rowid,text) VALUES (new.rowid,new.text); END;
                CREATE TRIGGER IF NOT EXISTS events_ad AFTER DELETE ON events BEGIN INSERT INTO event_search(event_search,rowid,text) VALUES('delete',old.rowid,old.text); END;
                CREATE TABLE IF NOT EXISTS usage (id TEXT NOT NULL, path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE, session_id TEXT NOT NULL, timestamp TEXT NOT NULL, fact TEXT NOT NULL, PRIMARY KEY(path,id));
                CREATE INDEX IF NOT EXISTS ix_usage_session ON usage(session_id);
                PRAGMA user_version=1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var sources = options.Sources ?? [Path.Combine(codexHome, "sessions"), Path.Combine(codexHome, "archived_sessions")];
            var defaults = new ViewerSettings([.. sources.Select(Path.GetFullPath)], "America/New_York", PricingDefaults.CreateCatalog());
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO configuration(key,value) VALUES('settings',$settings)";
            insert.Parameters.AddWithValue("$settings", JsonStorage.Serialize(defaults));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            if (await ReadValueAsync(connection, "copilot-pricing-v1", cancellationToken, transaction).ConfigureAwait(false) is null)
            {
                var settings = await ReadSettingsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
                var updated = settings with { Pricing = CopilotPricingDefaults.Merge(settings.Pricing) };
                await SetValueAsync(connection, "settings", JsonStorage.Serialize(updated), cancellationToken, transaction).ConfigureAwait(false);
                await SetValueAsync(connection, "copilot-pricing-v1", CopilotPricingDefaults.Version, cancellationToken, transaction).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    internal static async Task<string?> ReadValueAsync(SqliteConnection connection, string key, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM configuration WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SetValueAsync(SqliteConnection connection, string key, string value, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO configuration(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ViewerSettings> ReadSettingsAsync(SqliteConnection connection, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        var value = await ReadValueAsync(connection, "settings", cancellationToken, transaction).ConfigureAwait(false);
        if (value is not null)
        {
            return JsonStorage.Deserialize<ViewerSettings>(value);
        }

        throw new InvalidDataException("The viewer settings have not been initialized.");
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "session-viewer.db"),
            DefaultTimeout = 5,
            ForeignKeys = true,
            Pooling = true,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
