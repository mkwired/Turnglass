using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public sealed class SessionIndexer(ViewerDatabase database, TimeProvider clock, ILogger<SessionIndexer> logger)
{
    private static readonly Action<ILogger, string, Exception?> _fileError = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(SessionIndexer)), "A session file could not be indexed: {FileName}");

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var fileLock = database.TryAcquireLock();
        if (fileLock is null)
        {
            return;
        }

        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var settings = await ViewerDatabase.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var priorText = await ViewerDatabase.ReadValueAsync(connection, "index", cancellationToken).ConfigureAwait(false);
        var prior = priorText is null ? null : JsonStorage.Deserialize<IndexStatus>(priorText);
        var indexing = new IndexStatus(prior?.InitialIndexComplete ?? false, true, prior?.LastSuccessfulScanUtc, 0, 0, [], []);
        await ViewerDatabase.SetValueAsync(connection, "index", JsonStorage.Serialize(indexing), cancellationToken).ConfigureAwait(false);
        var statuses = new List<SourceStatus>();
        var warnings = new List<string>();
        var configuredSources = settings.Sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            foreach (var source in configuredSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var successful = true;
                string[] paths;
                try
                {
                    paths = Directory.GetFiles(source, "*.jsonl", SearchOption.AllDirectories);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    statuses.Add(new SourceStatus(source, false, 0, exception.Message));
                    warnings.Add($"Source unavailable: {source}");
                    continue;
                }

                var titles = await ReadTitlesAsync(source, cancellationToken).ConfigureAwait(false);
                foreach (var path in paths)
                {
                    try
                    {
                        await ImportFileAsync(connection, source, path, titles, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                    {
                        successful = false;
                        warnings.Add($"Could not index {Path.GetFileName(path)}: {exception.Message}");
                        _fileError(logger, Path.GetFileName(path), null);
                    }
                }

                if (successful)
                {
                    await RemoveMissingFilesAsync(connection, source, paths, cancellationToken).ConfigureAwait(false);
                }

                statuses.Add(new SourceStatus(source, successful, paths.Length, successful ? null : "Some files could not be indexed; retained the previous index for those files."));
            }

            await RemoveUnconfiguredSourcesAsync(connection, configuredSources, cancellationToken).ConfigureAwait(false);
            var state = new IndexStatus(statuses.All(status => status.Available), false, statuses.All(status => status.Available) ? clock.GetUtcNow() : prior?.LastSuccessfulScanUtc, 0, 0, statuses, [.. warnings.Take(50)]);
            await ViewerDatabase.SetValueAsync(connection, "index", JsonStorage.Serialize(state), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var failed = indexing with { IsIndexing = false, Warnings = ["The latest indexing pass was interrupted; previously committed data remains available."] };
            await ViewerDatabase.SetValueAsync(connection, "index", JsonStorage.Serialize(failed), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ImportFileAsync(SqliteConnection connection, string source, string path, IReadOnlyDictionary<string, string> titles, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checkpoint = await ReadCheckpointAsync(connection, path, cancellationToken).ConfigureAwait(false);
        var prefixLength = checkpoint is { PrefixLength: > 0 } ? checkpoint.PrefixLength : (int)Math.Min(stream.Length, 256);
        var prefix = new byte[prefixLength];
        var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, false, cancellationToken).ConfigureAwait(false);
        var prefixHash = Convert.ToHexStringLower(SHA256.HashData(prefix.AsSpan(0, read)));
        var tailHash = checkpoint is null ? null : await ReadTailHashAsync(stream, Math.Min(stream.Length, checkpoint.Offset), cancellationToken).ConfigureAwait(false);
        var reset = checkpoint is not null && (stream.Length < checkpoint.Offset || read != prefixLength || prefixHash != checkpoint.PrefixHash || (checkpoint.State.TailHash is not null && tailHash != checkpoint.State.TailHash));
        if (reset)
        {
            checkpoint = null;
            prefixLength = read;
        }

        var parser = checkpoint?.State ?? new ParserState { SessionId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path))) };
        var offset = checkpoint?.Offset ?? 0;
        var records = new List<ParsedRecord>();
        var firstBatch = true;
        await foreach (var line in JsonLines.ReadAsync(stream, offset, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                records.Add(RolloutParser.Parse(line.Text, parser));
            }

            offset = line.EndOffset;
            if (records.Count >= 100)
            {
                ApplyTitle(parser, titles);
                parser.TailHash = await ReadTailHashAsync(stream, offset, cancellationToken).ConfigureAwait(false);
                await CommitBatchAsync(connection, source, path, offset, prefixLength, prefixHash, parser, records, reset && firstBatch, cancellationToken).ConfigureAwait(false);
                firstBatch = false;
                records.Clear();
            }
        }

        ApplyTitle(parser, titles);
        parser.TailHash = await ReadTailHashAsync(stream, offset, cancellationToken).ConfigureAwait(false);
        await CommitBatchAsync(connection, source, path, offset, prefixLength, prefixHash, parser, records, reset && firstBatch, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadTailHashAsync(FileStream stream, long offset, CancellationToken cancellationToken)
    {
        var position = stream.Position;
        var length = (int)Math.Min(offset, 256);
        stream.Position = offset - length;
        var bytes = new byte[length];
        var read = await stream.ReadAtLeastAsync(bytes, length, false, cancellationToken).ConfigureAwait(false);
        stream.Position = position;
        return Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, read)));
    }

    private static void ApplyTitle(ParserState parser, IReadOnlyDictionary<string, string> titles)
    {
        if (titles.TryGetValue(parser.SessionId, out var title))
        {
            parser.Title = title;
        }
    }

    private static async Task CommitBatchAsync(SqliteConnection connection, string source, string path, long offset, int prefixLength, string prefixHash, ParserState state, List<ParsedRecord> records, bool reset, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        if (reset)
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM files WHERE path=$path";
            clear.Parameters.AddWithValue("$path", path);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ViewerDatabase.SetValueAsync(connection, "generation", Guid.NewGuid().ToString("N"), cancellationToken, transaction).ConfigureAwait(false);
        }

        using var checkpoint = connection.CreateCommand();
        checkpoint.Transaction = transaction;
        checkpoint.CommandText = "INSERT INTO files(path,source,offset,prefix_length,prefix_hash,state) VALUES($path,$source,$offset,$length,$hash,$state) ON CONFLICT(path) DO UPDATE SET source=excluded.source,offset=excluded.offset,prefix_length=excluded.prefix_length,prefix_hash=excluded.prefix_hash,state=excluded.state";
        checkpoint.Parameters.AddWithValue("$path", path);
        checkpoint.Parameters.AddWithValue("$source", source);
        checkpoint.Parameters.AddWithValue("$offset", offset);
        checkpoint.Parameters.AddWithValue("$length", prefixLength);
        checkpoint.Parameters.AddWithValue("$hash", prefixHash);
        checkpoint.Parameters.AddWithValue("$state", JsonStorage.Serialize(state));
        await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            if (record.Event is not null)
            {
                await InsertEventAsync(connection, transaction, path, record.Event, cancellationToken).ConfigureAwait(false);
            }

            if (record.RemoveUsageId is not null)
            {
                using var remove = connection.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM usage WHERE path=$path AND id=$id";
                remove.Parameters.AddWithValue("$path", path);
                remove.Parameters.AddWithValue("$id", record.RemoveUsageId);
                await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (record.Usage is not null)
            {
                using var usage = connection.CreateCommand();
                usage.Transaction = transaction;
                usage.CommandText = "INSERT OR IGNORE INTO usage(id,path,session_id,timestamp,fact) VALUES($id,$path,$session,$time,$fact)";
                usage.Parameters.AddWithValue("$id", record.Usage.Id);
                usage.Parameters.AddWithValue("$path", path);
                usage.Parameters.AddWithValue("$session", record.Usage.SessionId);
                usage.Parameters.AddWithValue("$time", record.Usage.Timestamp.ToString("O", CultureInfo.InvariantCulture));
                usage.Parameters.AddWithValue("$fact", JsonStorage.Serialize(record.Usage));
                await usage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction, string path, LogEvent entry, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO events(id,path,session_id,ordinal,timestamp,kind,role,text,raw,inherited,encrypted) VALUES($id,$path,$session,$ordinal,$timestamp,$kind,$role,$text,$raw,$inherited,$encrypted)";
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$session", entry.SessionId);
        command.Parameters.AddWithValue("$ordinal", entry.Ordinal);
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$kind", entry.Type);
        command.Parameters.AddWithValue("$role", (object?)entry.Role ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", entry.Text);
        command.Parameters.AddWithValue("$raw", entry.Raw);
        command.Parameters.AddWithValue("$inherited", entry.Inherited);
        command.Parameters.AddWithValue("$encrypted", entry.Encrypted);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileCheckpoint?> ReadCheckpointAsync(SqliteConnection connection, string path, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT offset,prefix_length,prefix_hash,state FROM files WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new FileCheckpoint(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), JsonStorage.Deserialize<ParserState>(reader.GetString(3))) : null;
    }

    private static async Task RemoveMissingFilesAsync(SqliteConnection connection, string source, string[] paths, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path FROM files WHERE source=$source";
            command.Parameters.AddWithValue("$source", source);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                if (!existing.Contains(path))
                {
                    stale.Add(path);
                }
            }
        }

        foreach (var path in stale)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM files WHERE path=$path";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RemoveUnconfiguredSourcesAsync(SqliteConnection connection, string[] sources, CancellationToken cancellationToken)
    {
        var stale = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT source FROM files";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var source = reader.GetString(0);
                if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase))
                {
                    stale.Add(source);
                }
            }
        }

        foreach (var source in stale)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM files WHERE source=$source";
            command.Parameters.AddWithValue("$source", source);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> ReadTitlesAsync(string source, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var parent = Directory.GetParent(source)?.FullName;
        if (parent is null)
        {
            return result;
        }

        var path = Path.Combine(parent, "session_index.jsonl");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await foreach (var line in JsonLines.ReadAsync(stream, 0, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var document = JsonDocument.Parse(line.Text);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) && root.TryGetProperty("thread_name", out var title) && id.ValueKind == JsonValueKind.String && title.ValueKind == JsonValueKind.String && id.GetString() is { } idText && title.GetString() is { } titleText)
                    {
                        result[idText] = titleText;
                    }
                }
                catch (JsonException)
                {
                    // A title is optional; the rollout remains the authoritative source.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Codex installations without a title index are supported.
        }

        return result;
    }

    private sealed record FileCheckpoint(long Offset, int PrefixLength, string PrefixHash, ParserState State);
}
