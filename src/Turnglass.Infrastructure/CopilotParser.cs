using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Turnglass.Core;

[assembly: InternalsVisibleTo("Turnglass.Tests")]

namespace Turnglass.Infrastructure;

internal enum CopilotSourceKind
{
    Sdk,
    VsCodeDebug,
}

internal sealed record CopilotSourceFile(string Path, string Client, CopilotSourceKind Kind, string SessionId, string? ParentSessionId = null);

internal sealed record CopilotParsedUsage(CopilotUsageFact Fact, CopilotSourceKind Kind, IReadOnlyList<string> CorrelationIds, string? ParentSessionId = null, long? AccountingSequence = null, bool HasResponseModel = false);

internal sealed class CopilotParserState(CopilotSourceFile source)
{
    public CopilotSourceFile Source { get; } = source;
    public string SessionId { get; set; } = source.SessionId;
    public string? ParentSessionId { get; set; } = source.ParentSessionId;
    public List<string> Warnings { get; } = [];
    public int SkippedRecords { get; private set; }
    public bool HasUsageRecords { get; set; }

    internal void Skip(string warning)
    {
        SkippedRecords++;
        Warn(warning);
    }

    internal void Warn(string warning)
    {
        if (Warnings.Count < 20 && !Warnings.Contains(warning, StringComparer.Ordinal))
        {
            Warnings.Add(warning);
        }
    }
}

internal static class CopilotParser
{
    internal static CopilotParsedUsage? Parse(string raw, CopilotParserState state)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                state.Skip("Non-object Copilot records were skipped.");
                return null;
            }

            var type = Text(root, "type");
            if (state.Source.Kind == CopilotSourceKind.Sdk)
            {
                if (type == "session.start")
                {
                    var data = Object(root, "data");
                    state.SessionId = Text(data, "sessionId") ?? state.SessionId;
                    state.ParentSessionId = Text(data, "parentSessionId") ?? state.ParentSessionId;
                    return null;
                }

                return type == "session.usage_record" ? ParseSdk(root, state) : null;
            }

            return type == "llm_request" ? ParseDebug(root, state) : null;
        }
        catch (JsonException)
        {
            state.Skip("Malformed Copilot JSON records were skipped.");
            return null;
        }
    }

    private static CopilotParsedUsage? ParseSdk(JsonElement root, CopilotParserState state)
    {
        state.HasUsageRecords = true;
        var usage = Object(Object(root, "data"), "usage");
        if (!TryCounters(usage, "cacheReadTokens", "cacheWriteTokens", out var input, out var output, out var cached, out var written, out var reasoning))
        {
            state.Skip("Copilot calls with invalid or incomplete token counts were excluded.");
            return null;
        }

        var timestampText = Text(root, "timestamp");
        if (timestampText is null || !timestampText.Contains('T') ||
            !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            state.Skip("Copilot calls without a valid timestamp were excluded.");
            return null;
        }

        var accounting = Object(usage, "accounting");
        var session = Text(accounting, "sourceSessionId") ?? state.SessionId;
        var usageId = Text(accounting, "usageId");
        var apiCallId = Text(usage, "apiCallId");
        var providerCallId = Text(usage, "providerCallId");
        _ = TryCounter(accounting, "sequence", false, out var sequence);
        var identity = usageId ?? apiCallId;
        var identityKind = "call";
        if (identity is null && sequence is not null)
        {
            identity = sequence.Value.ToString(CultureInfo.InvariantCulture);
            identityKind = "sequence";
        }

        if (string.IsNullOrWhiteSpace(session) || identity is null)
        {
            state.Skip("Copilot calls without a stable session and call identity were excluded.");
            return null;
        }

        var responseModel = Text(usage, "responseModel");
        var model = responseModel ?? Text(usage, "model");
        var lifetime = CacheLifetime(usage, written, state);
        var fact = new CopilotUsageFact(Identity("sdk", session, identityKind, identity), session, timestamp, model, state.Source.Client, input, output, cached, written, reasoning, CacheWriteLifetime: lifetime);
        return new CopilotParsedUsage(fact, CopilotSourceKind.Sdk, Aliases(usageId, apiCallId, providerCallId), session == state.SessionId ? state.ParentSessionId : null, sequence, responseModel is not null);
    }

    private static CopilotParsedUsage? ParseDebug(JsonElement root, CopilotParserState state)
    {
        state.HasUsageRecords = true;
        if (root.TryGetProperty("v", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1))
        {
            state.Skip("Unsupported Copilot debug log versions were excluded.");
            return null;
        }

        var attributes = Object(root, "attrs");
        if (!TryCounters(attributes, "cachedTokens", "cacheWriteTokens", out var input, out var output, out var cached, out var written, out var reasoning))
        {
            state.Skip("Copilot calls with invalid or incomplete token counts were excluded.");
            return null;
        }

        if (!TryCounter(root, "ts", true, out var milliseconds) || milliseconds is null || milliseconds > 253402300799999)
        {
            state.Skip("Copilot calls without a valid timestamp were excluded.");
            return null;
        }

        var session = Text(root, "sid") ?? state.SessionId;
        var spanId = Text(root, "spanId");
        if (string.IsNullOrWhiteSpace(session) || spanId is null || !TryCounter(root, "rIdx", false, out var restart))
        {
            state.Skip("Copilot debug calls without a stable session and span identity were excluded.");
            return null;
        }

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);
        var responseModel = Text(attributes, "responseModel");
        var model = responseModel ?? Text(attributes, "model");
        var parent = state.ParentSessionId ?? (session != state.Source.SessionId ? state.Source.SessionId : null);
        var fact = new CopilotUsageFact(Identity("debug", session, (restart ?? 0).ToString(CultureInfo.InvariantCulture), spanId), session, timestamp, model, state.Source.Client, input, output, cached, written, reasoning);
        return new CopilotParsedUsage(fact, CopilotSourceKind.VsCodeDebug, Aliases(Text(attributes, "responseId")), parent, HasResponseModel: responseModel is not null);
    }

    private static bool TryCounters(JsonElement value, string cachedName, string writtenName, out long input, out long output, out long? cached, out long? written, out long? reasoning)
    {
        input = 0;
        output = 0;
        cached = null;
        written = null;
        reasoning = null;
        if (!TryCounter(value, "inputTokens", true, out var inputValue) ||
            !TryCounter(value, "outputTokens", true, out var outputValue) ||
            !TryCounter(value, cachedName, false, out cached) ||
            !TryCounter(value, writtenName, false, out written) ||
            !TryCounter(value, "reasoningTokens", false, out reasoning))
        {
            return false;
        }

        input = inputValue!.Value;
        output = outputValue!.Value;
        return output <= long.MaxValue - input && cached.GetValueOrDefault() <= input &&
            written.GetValueOrDefault() <= input - cached.GetValueOrDefault() &&
            reasoning.GetValueOrDefault() <= output;
    }

    private static bool TryCounter(JsonElement value, string name, bool required, out long? result)
    {
        result = null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var counter) || counter.ValueKind == JsonValueKind.Null)
        {
            return !required;
        }

        if (counter.ValueKind != JsonValueKind.Number || !counter.TryGetInt64(out var number) || number < 0)
        {
            return false;
        }

        result = number;
        return true;
    }

    private static string? CacheLifetime(JsonElement usage, long? written, CopilotParserState state)
    {
        if (TryCounter(usage, "cacheTtlSeconds", false, out var seconds))
        {
            var lifetime = seconds switch
            {
                300 => "5m",
                3600 => "1h",
                1800 => "30m",
                43200 => "12h",
                _ => null,
            };
            if (lifetime is not null)
            {
                return lifetime;
            }
        }

        if (written > 0)
        {
            state.Warn("Some Copilot cache writes have no recognized cache lifetime; their pricing may be incomplete.");
        }

        return null;
    }

    private static string Identity(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));

    private static string[] Aliases(params string?[] values) =>
        [.. values.Where(value => value is not null).Select(value => value!).Distinct(StringComparer.Ordinal)];

    private static JsonElement Object(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;

    private static string? Text(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var child) || child.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = child.GetString();
        return !string.IsNullOrWhiteSpace(text) && text.Length <= 1024 && !text.Any(char.IsControl) ? text : null;
    }
}
