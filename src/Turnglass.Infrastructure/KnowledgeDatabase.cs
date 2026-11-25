using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Turnglass.Infrastructure;

/// <summary>Durable, independently versioned knowledge. Index rebuilds never touch this database.</summary>
public sealed class KnowledgeDatabase(ViewerOptions options) : IDisposable
{
    private readonly SemaphoreSlim _initialization = new(1);
    private bool _initialized;

    public void Dispose() => _initialization.Dispose();

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.DataDirectory, "knowledge.db"),
            DefaultTimeout = 15,
            ForeignKeys = true,
            Pooling = true,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_initialized)
                {
                    using var version = connection.CreateCommand();
                    version.CommandText = "PRAGMA user_version";
                    if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 1)
                    {
                        throw new InvalidOperationException("Knowledge was created by a newer viewer version. Upgrade both viewer hosts.");
                    }

                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        PRAGMA journal_mode=WAL;
                        BEGIN IMMEDIATE;
                        CREATE TABLE IF NOT EXISTS insights (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS checkpoints (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS packets (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS submissions (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS revisions (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                        PRAGMA user_version=1;
                        COMMIT;
                        """;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    _initialized = true;
                }
            }
            finally
            {
                _initialization.Release();
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

    private static string Table(string name) => name is "insights" or "checkpoints" or "packets" or "submissions" or "revisions" ? name : throw new ArgumentException("Unknown knowledge table.", nameof(name));
}
