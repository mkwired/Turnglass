using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public sealed class ViewerService(ViewerDatabase database, SessionIndexer indexer, TimeProvider clock) : IViewerService
{
    public async Task<ViewerSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ViewerDatabase.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ViewerSettings> UpdateSettingsAsync(ViewerSettings settings, CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var fileLock = await database.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var normalized = settings with { Sources = [.. settings.Sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)] };
        var previous = await ViewerDatabase.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await ViewerDatabase.SetValueAsync(connection, "settings", JsonStorage.Serialize(normalized), cancellationToken, transaction).ConfigureAwait(false);
        if (!previous.Sources.SequenceEqual(normalized.Sources, StringComparer.OrdinalIgnoreCase))
        {
            await ViewerDatabase.SetValueAsync(connection, "index", JsonStorage.Serialize(new IndexStatus(false, false, null, 0, 0, [], ["Sources changed; waiting for indexing."])), cancellationToken, transaction).ConfigureAwait(false);
            await ViewerDatabase.SetValueAsync(connection, "generation", Guid.NewGuid().ToString("N"), cancellationToken, transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageReport> GetUsageAsync(UsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var settings = await ViewerDatabase.ReadSettingsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var timezone = query.Timezone ?? settings.Timezone;
        var now = clock.GetUtcNow();
        var periods = UsagePeriods.GetPeriods(now, query.Mode, timezone);
        var sessions = snapshot.Sessions.Where(session => query.Project is null || session.Project == query.Project).Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        var facts = snapshot.Facts.Where(fact => sessions.Contains(fact.SessionId) && (query.Model is null || fact.Model == query.Model)).ToArray();
        var summaries = periods.Select(period => new PeriodUsage(period.Label, period.Start, period.End, PricingCalculator.Calculate(facts.Where(fact => fact.Timestamp >= period.Start && fact.Timestamp < period.End), settings.Pricing))).ToArray();
        var from = query.From ?? now.AddDays(-30);
        var to = query.To ?? now;
        if (from >= to || to - from > TimeSpan.FromDays(3660))
        {
            throw new ArgumentException("The chart date range must be increasing and no longer than ten years.", nameof(query));
        }

        var grouped = facts.Where(fact => fact.Timestamp >= from && fact.Timestamp < to).GroupBy(fact => UsagePeriods.GetDay(fact.Timestamp, timezone)).ToDictionary(group => group.Key, group => PricingCalculator.Calculate(group, settings.Pricing), StringComparer.Ordinal);
        var startDay = DateOnly.ParseExact(UsagePeriods.GetDay(from, timezone), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDay = DateOnly.ParseExact(UsagePeriods.GetDay(to.AddTicks(-1), timezone), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var buckets = new List<UsageBucket>();
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            buckets.Add(new UsageBucket(date, grouped.TryGetValue(date, out var total) ? total : PricingCalculator.Calculate([], settings.Pricing)));
        }

        return new UsageReport(query.Mode, timezone, now, settings.Pricing.Version, summaries, buckets, await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PageResult<SessionView>> ListSessionsAsync(SessionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateLimit(filter.Limit);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var scope = CursorScope(new SessionCursorScope(filter.Query, filter.Project, filter.Model, filter.From, filter.To));
        var generation = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var cursor = DecodeCursor(filter.Cursor, "sessions", scope, generation);
        var settings = await ViewerDatabase.ReadSettingsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var matching = snapshot.Sessions.Where(session => (filter.Project is null || session.Project == filter.Project)
            && (filter.Model is null || session.Model == filter.Model || snapshot.Facts.Any(fact => fact.SessionId == session.Id && fact.Model == filter.Model))
            && (filter.From is null || session.UpdatedAt >= filter.From)
            && (filter.To is null || session.StartedAt < filter.To)
            && (string.IsNullOrWhiteSpace(filter.Query) || session.Title.Contains(filter.Query, StringComparison.OrdinalIgnoreCase) || session.Id.Contains(filter.Query, StringComparison.OrdinalIgnoreCase) || (session.Project?.Contains(filter.Query, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Where(session => cursor is null || session.UpdatedAt < cursor.Timestamp || (session.UpdatedAt == cursor.Timestamp && StringComparer.Ordinal.Compare(session.Id, cursor.Id) > 0))
            .OrderByDescending(session => session.UpdatedAt).ThenBy(session => session.Id, StringComparer.Ordinal).Take(filter.Limit + 1).ToArray();
        var items = matching.Take(filter.Limit).Select(session => ToView(session, snapshot, settings.Pricing)).ToArray();
        var next = matching.Length > filter.Limit ? EncodeCursor(new PageCursor("sessions", scope, generation, items[^1].Session.Id, items[^1].Session.UpdatedAt)) : null;
        return new PageResult<SessionView>(items, next, await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false));
    }

    public async Task<SessionDetail?> GetSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var settings = await ViewerDatabase.ReadSettingsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var session = snapshot.Sessions.FirstOrDefault(item => item.Id == id);
        if (session is null)
        {
            return null;
        }

        return new SessionDetail(ToView(session, snapshot, settings.Pricing), [.. snapshot.Sessions.Where(child => child.ParentId == id).Select(child => ToView(child, snapshot, settings.Pricing))], await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PageResult<EventView>> ReadEventsAsync(string sessionId, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidateLimit(limit);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var scope = CursorScope(new EventCursorScope(sessionId));
        var generation = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var position = DecodeCursor(cursor, "events", scope, generation);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH response_ids AS MATERIALIZED (
              SELECT inherited,json_extract(raw,'$.payload.id') AS response_id
              FROM events WHERE session_id=$session
                AND kind IN ('message','reasoning','function_call','custom_tool_call','function_call_output','custom_tool_call_output')
                AND json_extract(raw,'$.payload.id') IS NOT NULL
              GROUP BY inherited,response_id)
            SELECT DISTINCT e.id,e.session_id,e.ordinal,e.timestamp,e.kind,e.role,substr(e.text,1,2000),e.inherited,e.encrypted,length(e.text)>2000
            FROM events AS e WHERE e.session_id=$session
            AND ($ordinal IS NULL OR e.ordinal>$ordinal OR (e.ordinal=$ordinal AND e.id>$id))
            AND NOT (e.kind IN ('event:item_completed','event:user_message','event:agent_message','event:agent_reasoning')
              AND ((json_extract(e.raw,'$.payload.item.id') IS NOT NULL
                AND EXISTS(SELECT 1 FROM response_ids WHERE response_ids.inherited=e.inherited AND response_ids.response_id=json_extract(e.raw,'$.payload.item.id')))
              OR (e.text<>'' AND EXISTS(SELECT 1 FROM events AS other
                WHERE other.session_id=e.session_id AND other.ordinal BETWEEN e.ordinal-3 AND e.ordinal+3
                  AND other.inherited=e.inherited AND other.text=e.text
                  AND other.kind IN ('message','reasoning','function_call','custom_tool_call','function_call_output','custom_tool_call_output')
                  AND abs(unixepoch(e.timestamp)-unixepoch(other.timestamp))<=5
                  AND ((e.kind='event:user_message' AND other.kind='message' AND other.role='user')
                    OR (e.kind='event:agent_message' AND other.kind='message' AND other.role='assistant')
                    OR (e.kind='event:agent_reasoning' AND other.kind='reasoning')
                    OR (e.kind='event:item_completed' AND (e.role IS NULL OR e.role=other.role)))))))
            ORDER BY e.ordinal,e.id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$ordinal", (object?)position?.Ordinal ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", position?.Id ?? string.Empty);
        var rows = await ReadEventViewsAsync(command, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var next = rows.Count > limit ? EncodeCursor(new PageCursor("events", scope, generation, rows[limit - 1].Id, Ordinal: rows[limit - 1].Ordinal)) : null;
        return new PageResult<EventView>([.. rows.Take(limit)], next, await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false));
    }

    public async Task<LogChunk?> ReadLogRecordAsync(string sessionId, string eventId, int offset = 0, int limit = 16000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 32000);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw FROM events WHERE session_id=$session AND id=$id LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$id", eventId);
        var raw = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, raw.Length);
        if (offset > 0 && offset < raw.Length && char.IsHighSurrogate(raw[offset - 1]) && char.IsLowSurrogate(raw[offset]))
        {
            throw new ArgumentException("The offset must be at a Unicode character boundary. Use the returned nextOffset.", nameof(offset));
        }

        var length = Math.Min(limit, raw.Length - offset);
        if (length > 0 && offset + length < raw.Length && char.IsHighSurrogate(raw[offset + length - 1]) && char.IsLowSurrogate(raw[offset + length]))
        {
            if (length == 1)
            {
                throw new ArgumentException("The limit must be at least two to read this Unicode character.", nameof(limit));
            }

            length--;
        }

        return new LogChunk(eventId, raw.Substring(offset, length), offset + length < raw.Length ? offset + length : null, raw.Length);
    }

    public async Task<PageResult<EventView>> SearchLogsAsync(string query, string? sessionId = null, string? cursor = null, int limit = 30, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Length, 1000);
        ValidateLimit(limit);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var scope = CursorScope(new SearchCursorScope(query, sessionId));
        var generation = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var position = DecodeCursor(cursor, "search", scope, generation);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.id,e.session_id,e.ordinal,e.timestamp,e.kind,e.role,substr(e.text,1,2000),e.inherited,e.encrypted,length(e.text)>2000
            FROM event_search JOIN events AS e ON e.rowid=event_search.rowid
            WHERE event_search MATCH $query AND ($session IS NULL OR e.session_id=$session)
            AND ($timestamp IS NULL OR julianday(e.timestamp)<julianday($timestamp) OR (julianday(e.timestamp)=julianday($timestamp) AND e.id>$id))
            GROUP BY e.id ORDER BY julianday(e.timestamp) DESC,e.id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", $"\"{query.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$timestamp", (object?)position?.Timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", position?.Id ?? string.Empty);
        var rows = await ReadEventViewsAsync(command, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var next = rows.Count > limit ? EncodeCursor(new PageCursor("search", scope, generation, rows[limit - 1].Id, rows[limit - 1].Timestamp)) : null;
        return new PageResult<EventView>([.. rows.Take(limit)], next, await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false));
    }

    public async Task<FilterOptions> GetFiltersAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return new FilterOptions([.. snapshot.Sessions.Select(session => session.Project).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], [.. snapshot.Sessions.Select(session => session.Model).Concat(snapshot.Facts.Select(fact => fact.Model)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using (var fileLock = await database.AcquireLockAsync(cancellationToken).ConfigureAwait(false))
        {
            using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM files; DELETE FROM configuration WHERE key='index';";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ViewerDatabase.SetValueAsync(connection, "generation", Guid.NewGuid().ToString("N"), cancellationToken, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await indexer.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Snapshot> ReadSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var states = new List<ParserState>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT state FROM files";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                states.Add(JsonStorage.Deserialize<ParserState>(reader.GetString(0)));
            }
        }

        var facts = new List<UsageFact>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT fact FROM usage GROUP BY id";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                facts.Add(JsonStorage.Deserialize<UsageFact>(reader.GetString(0)));
            }
        }

        var sessions = states.Where(state => state.HasMetadata).GroupBy(state => state.SessionId, StringComparer.Ordinal).Select(group => group.MaxBy(state => state.UpdatedAt)!.ToSession()).ToArray();
        return new Snapshot(sessions, facts, [.. states.SelectMany(state => state.Warnings).Distinct(StringComparer.Ordinal)]);
    }

    private static async Task<IndexStatus> ReadStatusAsync(SqliteConnection connection, SqliteTransaction transaction, Snapshot snapshot, CancellationToken cancellationToken)
    {
        var serialized = await ViewerDatabase.ReadValueAsync(connection, "index", cancellationToken, transaction).ConfigureAwait(false);
        var status = serialized is null ? new IndexStatus(false, false, null, 0, 0, [], []) : JsonStorage.Deserialize<IndexStatus>(serialized);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(DISTINCT id) FROM events";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return status with { SessionCount = snapshot.Sessions.Count, EventCount = count, Warnings = [.. status.Warnings.Concat(snapshot.Warnings).Distinct(StringComparer.Ordinal)] };
    }

    private static SessionView ToView(SessionRecord session, Snapshot snapshot, PricingCatalog catalog)
    {
        var family = new HashSet<string>(StringComparer.Ordinal) { session.Id };
        var queue = new Queue<string>();
        queue.Enqueue(session.Id);
        while (queue.TryDequeue(out var parent))
        {
            foreach (var child in snapshot.Sessions.Where(item => item.ParentId == parent))
            {
                if (family.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return new SessionView(session, PricingCalculator.Calculate(snapshot.Facts.Where(fact => fact.SessionId == session.Id), catalog), PricingCalculator.Calculate(snapshot.Facts.Where(fact => family.Contains(fact.SessionId)), catalog), family.Count - 1);
    }

    private static async Task<List<EventView>> ReadEventViewsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<EventView>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = reader.GetString(6);
            results.Add(new EventView(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), text, reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9)));
        }

        return results;
    }

    private static void ValidateLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
    }

    private static PageCursor? DecodeCursor(string? cursor, string kind, string scope, string generation)
    {
        if (cursor is null)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(cursor.Length, 2048);
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var value = JsonStorage.Deserialize<PageCursor>(decoded);
            if (value.Kind == kind && value.Scope == scope && value.Generation == generation && !string.IsNullOrEmpty(value.Id)
                && (kind == "events" ? value.Ordinal is >= 0 : value.Timestamp is not null))
            {
                return value;
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidDataException)
        {
            // A cursor is only valid for its original query and index generation.
        }

        throw new ArgumentException("Invalid or stale pagination cursor. Restart from the first page.", nameof(cursor));
    }

    private static async Task<string> ReadGenerationAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) => await ViewerDatabase.ReadValueAsync(connection, "generation", cancellationToken, transaction).ConfigureAwait(false) ?? "0";

    private static string CursorScope<T>(T query) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonStorage.Serialize(query))));

    private static string EncodeCursor(PageCursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonStorage.Serialize(cursor)));

    internal sealed record PageCursor(string Kind, string Scope, string Generation, string Id, DateTimeOffset? Timestamp = null, long? Ordinal = null);

    // These shapes and property order preserve cursors issued by previous hosts.
    internal sealed record SessionCursorScope(string? Query, string? Project, string? Model, DateTimeOffset? From, DateTimeOffset? To);

    internal sealed record EventCursorScope(string SessionId);

    internal sealed record SearchCursorScope(string Query, string? SessionId);

    private sealed record Snapshot(IReadOnlyList<SessionRecord> Sessions, IReadOnlyList<UsageFact> Facts, IReadOnlyList<string> Warnings);
}
