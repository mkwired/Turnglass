using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

/// <summary>Durable Copilot usage retained independently of disposable session indexes and source logs.</summary>
public sealed class CopilotDatabase(ViewerOptions options) : IDisposable
{
    private readonly SemaphoreSlim _initialization = new(1);
    private bool _initialized;

    internal string DataDirectory { get; } = Path.GetFullPath(options.DataDirectory);

    public void Dispose() => _initialization.Dispose();

    internal FileStream? TryAcquireLock()
    {
        Directory.CreateDirectory(DataDirectory);
        try
        {
            return new FileStream(Path.Combine(DataDirectory, "copilot-index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
                throw new TimeoutException("The Copilot usage ledger is busy. Retry after the current scan finishes.");
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
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 1)
            {
                throw new InvalidOperationException("Copilot usage was created by a newer viewer version. Upgrade both viewer hosts.");
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                BEGIN IMMEDIATE;
                CREATE TABLE IF NOT EXISTS configuration (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS sources (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS checkpoints (id TEXT PRIMARY KEY COLLATE NOCASE, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS facts (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS observations (fact_id TEXT NOT NULL, source_id TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, observed_at TEXT NOT NULL, PRIMARY KEY(fact_id,source_id,path));
                PRAGMA user_version=1;
                COMMIT;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var autoDiscover = options.CopilotSources is null && options.Sources is null
                && !string.Equals(Environment.GetEnvironmentVariable("TURNGLASS_COPILOT_AUTO_DISCOVER"), "false", StringComparison.OrdinalIgnoreCase);
            var defaults = new CopilotSettings(autoDiscover, options.CopilotSources ?? []);
            using var settings = connection.CreateCommand();
            settings.CommandText = "INSERT OR IGNORE INTO configuration(id,value) VALUES('settings',$value)";
            settings.Parameters.AddWithValue("$value", JsonStorage.Serialize(defaults));
            await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T?> ReadAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string table, string id, CancellationToken cancellationToken) where T : class
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT value FROM {Table(table)} WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        var value = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : JsonStorage.Deserialize<T>(value);
    }

    internal static async Task<List<T>> ReadAllAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT value FROM {Table(table)} ORDER BY id";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(JsonStorage.Deserialize<T>(reader.GetString(0)));
        }

        return result;
    }

    internal static async Task SaveAsync<T>(SqliteConnection connection, SqliteTransaction transaction, string table, string id, T value, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Table(table)}(id,value) VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$value", JsonStorage.Serialize(value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CopilotSettings> ReadSettingsAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        return await ReadAsync<CopilotSettings>(connection, transaction, "configuration", "settings", cancellationToken).ConfigureAwait(false)
            ?? new CopilotSettings(false, []);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "copilot-usage.db"),
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

    private static string Table(string name) => name is "configuration" or "sources" or "checkpoints" or "facts" ? name : throw new ArgumentException("Unknown Copilot usage table.", nameof(name));
}
