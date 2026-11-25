using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

internal sealed class ParserState
{
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = "Untitled session";
    public string? Project { get; set; }
    public string? Model { get; set; }
    public string? Tier { get; set; }
    public string? ParentId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long LineNumber { get; set; }
    public long? HistoryBoundary { get; set; }
    public bool HasMetadata { get; set; }
    public bool HasModernUsage { get; set; }
    public string? TailHash { get; set; }
    public TokenUsage Baseline { get; set; } = new();
    public string? LastLegacyId { get; set; }
    public TokenUsage? LastLegacyUsage { get; set; }
    public Dictionary<string, string> TurnModels { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public SessionRecord ToSession() => new(SessionId, Title, Project, Model, ParentId, StartedAt, UpdatedAt);

    public void Warn(string text)
    {
        if (!Warnings.Contains(text, StringComparer.Ordinal) && Warnings.Count < 30)
        {
            Warnings.Add(text);
        }
    }
}

internal sealed record ParsedRecord(LogEvent? Event, UsageFact? Usage = null, string? RemoveUsageId = null);

internal static class RolloutParser
{
    internal static ParsedRecord Parse(string raw, ParserState state)
    {
        state.LineNumber++;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            state.Warn("Malformed JSON records were skipped.");
            return new ParsedRecord(null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                state.Warn("Non-object JSON records were skipped.");
                return new ParsedRecord(null);
            }

            var type = Text(root, "type") ?? "unknown";
            var payload = Object(root, "payload");
            var validTimestamp = DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTime);
            var timestamp = validTimestamp ? parsedTime : state.UpdatedAt;
            if (!validTimestamp)
            {
                state.Warn("Records with missing or invalid timestamps were preserved; their usage was excluded.");
            }
            var ordinal = Number(root, "ordinal", state.LineNumber);
            if (type == "session_meta" && !state.HasMetadata)
            {
                state.HasMetadata = true;
                state.SessionId = Text(payload, "id") ?? state.SessionId;
                state.Title = Text(payload, "title") ?? Text(payload, "thread_name") ?? state.Title;
                state.Project = Text(payload, "cwd");
                state.ParentId = Text(payload, "parent_thread_id") ?? Text(payload, "forked_from_id") ?? FindText(Object(payload, "source"), "parent_thread_id");
                state.HistoryBoundary = NullableNumber(payload, "subagent_history_start_ordinal");
                state.StartedAt = timestamp;
            }

            if (state.StartedAt == default)
            {
                state.StartedAt = timestamp;
            }

            state.UpdatedAt = timestamp > state.UpdatedAt ? timestamp : state.UpdatedAt;
            var inherited = state.HistoryBoundary is not null && ordinal < state.HistoryBoundary.Value;
            if (type == "turn_context" && !inherited)
            {
                var model = Text(payload, "model");
                state.Model = model ?? state.Model;
                state.Tier = Text(payload, "service_tier") ?? state.Tier;
                state.Project ??= Text(payload, "cwd");
                var turnId = Text(payload, "turn_id");
                if (turnId is not null && model is not null)
                {
                    state.TurnModels[turnId] = model;
                }
            }

            if (type == "event_msg" && Text(payload, "type") == "thread_settings_applied" && !inherited)
            {
                var threadId = Text(payload, "thread_id");
                if (threadId is null || threadId == state.SessionId)
                {
                    var settings = Object(payload, "thread_settings");
                    state.Model = Text(settings, "model") ?? state.Model;
                    state.Tier = Text(settings, "service_tier") ?? state.Tier;
                }
            }

            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{state.SessionId}\n{ordinal.ToString(CultureInfo.InvariantCulture)}\n{raw}")));
            var role = Text(payload, "role") ?? Text(Object(payload, "item"), "role");
            var kind = type == "response_item" ? Text(payload, "type") ?? type : type == "event_msg" ? $"event:{Text(payload, "type") ?? "unknown"}" : type;
            role ??= kind switch
            {
                "event:user_message" => "user",
                "event:agent_message" or "event:agent_reasoning" => "assistant",
                _ => null,
            };
            var body = ExtractText(payload);
            var encrypted = FindText(payload, "encrypted_content") is not null;
            if (encrypted && string.IsNullOrWhiteSpace(body))
            {
                body = "Encrypted reasoning is unavailable.";
            }

            if (state.Title == "Untitled session" && role == "user" && !inherited && body.Length > 0)
            {
                state.Title = body[..Math.Min(body.Length, 100)].Replace('\n', ' ').Replace('\r', ' ');
            }

            var logEvent = new LogEvent(id, state.SessionId, ordinal, timestamp, kind, role, body, raw, inherited, encrypted);
            if (type == "token_usage_record")
            {
                if (!validTimestamp)
                {
                    return new ParsedRecord(logEvent);
                }

                return ParseModern(payload, state, logEvent, inherited);
            }

            if (type == "event_msg" && Text(payload, "type") == "token_count")
            {
                if (!validTimestamp)
                {
                    return new ParsedRecord(logEvent);
                }

                return ParseLegacy(payload, state, logEvent, inherited);
            }

            return new ParsedRecord(logEvent);
        }
    }

    private static ParsedRecord ParseModern(JsonElement payload, ParserState state, LogEvent logEvent, bool inherited)
    {
        var owner = Text(payload, "thread_id") ?? state.SessionId;
        if (inherited || owner != state.SessionId)
        {
            return new ParsedRecord(logEvent);
        }

        var usageElement = Object(payload, "usage");
        var tokens = Tokens(usageElement);
        if (!HasRequiredCounts(usageElement) || !IsValid(tokens))
        {
            state.Warn("Invalid token counts were excluded from usage totals.");
            return new ParsedRecord(logEvent);
        }

        var removeId = default(string);
        if (!state.HasModernUsage && state.LastLegacyUsage == tokens)
        {
            var cumulative = Tokens(Object(payload, "thread_token_usage"));
            if (cumulative == state.Baseline)
            {
                removeId = state.LastLegacyId;
            }
        }

        if (!state.HasModernUsage && state.LastLegacyId is not null && removeId is null)
        {
            var cumulative = Tokens(Object(payload, "thread_token_usage"));
            if (cumulative.TotalTokens > 0 && cumulative.TotalTokens != state.Baseline.TotalTokens + tokens.TotalTokens)
            {
                state.Warn("Legacy/modern usage transition is ambiguous; usage totals may be incomplete.");
            }
        }

        state.HasModernUsage = true;
        var turnId = Text(payload, "turn_id");
        var model = Text(payload, "model") ?? (turnId is not null && state.TurnModels.TryGetValue(turnId, out var turnModel) ? turnModel : state.Model);
        var responseId = Text(payload, "response_id");
        if (responseId is null)
        {
            state.Warn("A usage record has no response ID; deduplication uses its record identity.");
        }

        var usage = new UsageFact(responseId ?? logEvent.Id, owner, logEvent.Timestamp, model, Text(payload, "service_tier") ?? state.Tier, tokens, true, Text(payload, "cache_write_lifetime"));
        return new ParsedRecord(logEvent, usage, removeId);
    }

    private static ParsedRecord ParseLegacy(JsonElement payload, ParserState state, LogEvent logEvent, bool inherited)
    {
        var info = Object(payload, "info");
        var cumulativeElement = Object(info, "total_token_usage");
        if (cumulativeElement.ValueKind != JsonValueKind.Object)
        {
            return new ParsedRecord(logEvent);
        }

        var cumulative = Tokens(cumulativeElement);
        if (!HasRequiredCounts(cumulativeElement) || !IsValid(cumulative))
        {
            state.Warn("Invalid legacy token counters were excluded.");
            return new ParsedRecord(logEvent);
        }

        var delta = new TokenUsage(cumulative.InputTokens - state.Baseline.InputTokens, cumulative.CachedInputTokens - state.Baseline.CachedInputTokens, cumulative.CacheWriteInputTokens - state.Baseline.CacheWriteInputTokens, cumulative.OutputTokens - state.Baseline.OutputTokens, cumulative.ReasoningOutputTokens - state.Baseline.ReasoningOutputTokens);
        state.Baseline = cumulative;
        if (state.HasModernUsage || inherited)
        {
            return new ParsedRecord(logEvent);
        }

        if (state.ParentId is not null && state.HistoryBoundary is null)
        {
            state.Warn("Legacy usage from a fork with an unknown inherited boundary was excluded.");
            return new ParsedRecord(logEvent);
        }

        if (!IsValid(delta))
        {
            state.Warn("A cumulative counter reset was observed; the uncertain interval was excluded.");
            return new ParsedRecord(logEvent);
        }

        if (delta.TotalTokens == 0)
        {
            return new ParsedRecord(logEvent);
        }

        var last = Tokens(Object(info, "last_token_usage"));
        state.LastLegacyUsage = delta;
        state.LastLegacyId = $"legacy:{logEvent.Id}";
        return new ParsedRecord(logEvent, new UsageFact(state.LastLegacyId, state.SessionId, logEvent.Timestamp, state.Model, state.Tier, delta, last == delta));
    }

    private static bool HasRequiredCounts(JsonElement element) => NullableNumber(element, "input_tokens") is not null && NullableNumber(element, "output_tokens") is not null;

    private static bool IsValid(TokenUsage tokens) => tokens.InputTokens >= 0 && tokens.OutputTokens >= 0 && tokens.InputTokens <= long.MaxValue - tokens.OutputTokens && tokens.CachedInputTokens >= 0 && tokens.CacheWriteInputTokens >= 0 && tokens.ReasoningOutputTokens >= 0 && tokens.CachedInputTokens <= tokens.InputTokens && tokens.CacheWriteInputTokens <= tokens.InputTokens - tokens.CachedInputTokens && tokens.ReasoningOutputTokens <= tokens.OutputTokens;

    private static TokenUsage Tokens(JsonElement element) => new(Number(element, "input_tokens"), Number(element, "cached_input_tokens"), Number(element, "cache_write_input_tokens"), Number(element, "output_tokens"), Number(element, "reasoning_output_tokens"));

    private static long Number(JsonElement element, string name, long fallback = 0) => NullableNumber(element, name) ?? fallback;

    private static long? NullableNumber(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : null;

    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement Object(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? FindText(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var value = Text(element, name);
            if (value is not null)
            {
                return value;
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindText(property.Value, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var found = FindText(child, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string ExtractText(JsonElement element)
    {
        var parts = new List<string>();
        AppendText(element, parts);
        return string.Join('\n', parts);
    }

    private static void AppendText(JsonElement element, List<string> parts)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                AppendText(child, parts);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "text" or "message" or "arguments" or "input" or "output" or "summary" or "content" or "item" or "name" or "call_id")
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var text = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            parts.Add(property.Name switch
                            {
                                "name" => $"Tool: {text}",
                                "call_id" => $"Call: {text}",
                                _ => text,
                            });
                        }
                    }
                    else
                    {
                        AppendText(property.Value, parts);
                    }
                }
            }
        }
    }
}
