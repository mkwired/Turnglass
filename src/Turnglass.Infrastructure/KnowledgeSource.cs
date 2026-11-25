using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

internal static class KnowledgeSource
{
    internal static async Task<List<SessionRecord>> SessionsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM files";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var states = new List<ParserState>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states.Add(JsonStorage.Deserialize<ParserState>(reader.GetString(0)));
        }

        return [.. states.Where(state => state.HasMetadata).GroupBy(state => state.SessionId, StringComparer.Ordinal).Select(group => group.MaxBy(state => state.UpdatedAt)!.ToSession())];
    }

    internal static async Task<List<LogEvent>> EventsAsync(SqliteConnection connection, SqliteTransaction transaction, string sessionId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT id,session_id,ordinal,timestamp,kind,role,text,raw,inherited,encrypted FROM events WHERE session_id=$session ORDER BY ordinal,id";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LogEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LogEvent(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetBoolean(8), reader.GetBoolean(9)));
        }

        return result;
    }

    internal static bool Eligible(LogEvent item, IReadOnlyList<LogEvent> events, SessionRecord? session = null)
    {
        if (item.Inherited || item.Encrypted || session?.ParentId is not null && item.Timestamp < session.StartedAt || string.IsNullOrWhiteSpace(item.Text) || IsEnvelope(item.Text) || item.Role is "system" or "developer")
        {
            return false;
        }

        if (item.Type is not ("message" or "event:user_message" or "event:agent_message" or "event:item_completed" or "function_call_output" or "custom_tool_call_output" or "tool_result"))
        {
            return false;
        }

        using var document = JsonDocument.Parse(item.Raw);
        if (document.RootElement.TryGetProperty("payload", out var payload) && payload.TryGetProperty("channel", out var channel) && channel.GetString() == "analysis")
        {
            return false;
        }

        if (item.Type == "event:item_completed" && payload.TryGetProperty("item", out var completed) && completed.TryGetProperty("id", out var completedId))
        {
            var id = completedId.GetString();
            if (id is not null && events.Any(other => other.Id != item.Id && !other.Type.StartsWith("event:", StringComparison.Ordinal) && other.Inherited == item.Inherited && ResponseId(other.Raw) == id))
            {
                return false;
            }
        }

        // Event notifications can mirror response items; retain only one independent observation.
        if (item.Type.StartsWith("event:", StringComparison.Ordinal) && events.Any(other => other.Id != item.Id && other.Type == "message" && other.Role == item.Role && other.Inherited == item.Inherited && other.Text == item.Text && Math.Abs(other.Ordinal - item.Ordinal) <= 3 && (other.Timestamp - item.Timestamp).Duration() <= TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        return true;
    }

    internal static string Origin(LogEvent item, SessionRecord session)
    {
        if (item.Type is "function_call_output" or "custom_tool_call_output" or "tool_result" || item.Role == "tool")
        {
            return "tool";
        }

        if (item.Role == "assistant")
        {
            return "assistant";
        }

        // A child task's user role is normally an agent instruction, not a human preference.
        return item.Role == "user" && session.ParentId is null && !IsEnvelope(item.Text) ? "user" : "unknown";
    }

    internal static bool IsEnvelope(string text)
    {
        var trimmed = text.TrimStart();
        string[] prefixes = ["<environment_context>", "<environment_details>", "<user_instructions>", "<permissions instructions>", "<app-context>", "<system", "<developer", "<collaboration_mode>", "<multi_agent", "<turn_aborted>", "<subagent_notification>", "# AGENTS.md instructions", "# Repository Guidelines", "You are a coding agent", "You are an AI assistant", "Message Type: NEW_TASK", "Message Type: MESSAGE", "Message Type: FINAL_ANSWER"];
        return prefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || (trimmed.StartsWith('<') && (trimmed.Contains("<environment_context>", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("<subagent_notification>", StringComparison.OrdinalIgnoreCase)))
            || trimmed.StartsWith('[') && trimmed.Contains("subagent", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("task", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static string Anchor(IReadOnlyList<LogEvent> events, int count) => Hash(string.Join('\n', events.Take(count).Select(item => item.Id)));

    private static string? ResponseId(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.TryGetProperty("payload", out var payload) && payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }
}
