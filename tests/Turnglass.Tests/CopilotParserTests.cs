using System.Text.Json;
using System.Text.Json.Nodes;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class CopilotParserTests
{
    [Fact]
    public void SdkUsageKeepsActualCountersAndModelWithoutPersistingConversationContent()
    {
        var state = SdkState();
        var record = SdkRecord();
        record["data"]!["usage"]!["responseModel"] = "claude-haiku-4-5-20251001";
        record["data"]!["usage"]!["reasoningTokens"] = 12;
        record["data"]!["usage"]!["cacheTtlSeconds"] = 300;
        record["data"]!["usage"]!["cost"] = 0.33;
        record["data"]!["usage"]!["privateConversation"] = "do not persist this text";

        var result = CopilotParser.Parse(record.ToJsonString(), state);

        Assert.NotNull(result);
        Assert.Equal(60607, result.Fact.InputTokens);
        Assert.Equal(221, result.Fact.OutputTokens);
        Assert.Equal(60210, result.Fact.CachedInputTokens);
        Assert.Equal(391, result.Fact.CacheWriteInputTokens);
        Assert.Equal(12, result.Fact.ReasoningOutputTokens);
        Assert.Equal("claude-haiku-4-5-20251001", result.Fact.Model);
        Assert.Equal("5m", result.Fact.CacheWriteLifetime);
        Assert.True(result.HasResponseModel);
        Assert.Equal(2, result.AccountingSequence);
        Assert.Equal("shared", result.Fact.Client);
        Assert.Equal(["call-a", "provider-a"], result.CorrelationIds);
        Assert.DoesNotContain("do not persist", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("0.33", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(0, state.SkippedRecords);
    }

    [Fact]
    public void CopiesUseSourceSessionAndCallIdentityRatherThanContainingFileOrEventId()
    {
        var original = SdkRecord();
        var copy = SdkRecord();
        copy["id"] = "copied-event";
        copy["data"]!["usage"]!["accounting"]!["usageId"] = null;
        var first = CopilotParser.Parse(original.ToJsonString(), SdkState("original-file"));
        var second = CopilotParser.Parse(copy.ToJsonString(), SdkState("archive-copy"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Fact.Id, second.Fact.Id);
        Assert.Equal("source-session", second.Fact.SessionId);
    }

    [Fact]
    public void SequenceFallbackIsStableAndSeparateFromCallIds()
    {
        var sequence = SdkRecord();
        sequence["data"]!["usage"]!["accounting"]!["usageId"] = null;
        sequence["data"]!["usage"]!["apiCallId"] = null;
        var first = CopilotParser.Parse(sequence.ToJsonString(), SdkState());
        sequence["timestamp"] = "2026-11-22T17:46:02.524Z";
        var duplicate = CopilotParser.Parse(sequence.ToJsonString(), SdkState());
        var call = SdkRecord();
        call["data"]!["usage"]!["accounting"]!["usageId"] = "2";
        var separate = CopilotParser.Parse(call.ToJsonString(), SdkState());

        Assert.NotNull(first);
        Assert.NotNull(duplicate);
        Assert.NotNull(separate);
        Assert.Equal(first.Fact.Id, duplicate.Fact.Id);
        Assert.NotEqual(first.Fact.Id, separate.Fact.Id);
    }

    [Fact]
    public void AccountingSequenceRemainsAvailableWhenCallIdentityIsEnriched()
    {
        var sequenceOnly = SdkRecord();
        sequenceOnly["data"]!["usage"]!["accounting"]!["usageId"] = null;
        sequenceOnly["data"]!["usage"]!["apiCallId"] = null;
        var first = CopilotParser.Parse(sequenceOnly.ToJsonString(), SdkState());
        var enriched = CopilotParser.Parse(SdkRecord().ToJsonString(), SdkState());

        Assert.NotNull(first);
        Assert.NotNull(enriched);
        Assert.Equal(2, first.AccountingSequence);
        Assert.Equal(first.AccountingSequence, enriched.AccountingSequence);
        Assert.DoesNotContain("2", enriched.CorrelationIds);
        Assert.False(enriched.HasResponseModel);
    }

    [Theory]
    [InlineData("inputTokens", "-1")]
    [InlineData("inputTokens", "1.5")]
    [InlineData("inputTokens", "\"100\"")]
    [InlineData("inputTokens", "null")]
    [InlineData("inputTokens", "9223372036854775808")]
    [InlineData("outputTokens", "9223372036854775807")]
    [InlineData("outputTokens", "null")]
    [InlineData("cacheReadTokens", "60608")]
    [InlineData("cacheReadTokens", "60607")]
    [InlineData("cacheWriteTokens", "-1")]
    [InlineData("cacheWriteTokens", "1.25")]
    [InlineData("reasoningTokens", "222")]
    [InlineData("reasoningTokens", "\"12\"")]
    public void InvalidCountersAreExcludedInsteadOfCoerced(string field, string value)
    {
        var record = SdkRecord();
        record["data"]!["usage"]![field] = JsonNode.Parse(value);
        var state = SdkState();

        Assert.Null(CopilotParser.Parse(record.ToJsonString(), state));
        Assert.Equal(1, state.SkippedRecords);
        Assert.Single(state.Warnings);
    }

    [Fact]
    public void UnknownCacheCountsRemainDifferentFromMeasuredZero()
    {
        var record = SdkRecord();
        var usage = record["data"]!["usage"]!.AsObject();
        usage.Remove("cacheReadTokens");
        usage.Remove("cacheWriteTokens");
        var unknown = CopilotParser.Parse(record.ToJsonString(), SdkState());
        usage["cacheReadTokens"] = 0;
        usage["cacheWriteTokens"] = 0;
        var measured = CopilotParser.Parse(record.ToJsonString(), SdkState());

        Assert.NotNull(unknown);
        Assert.NotNull(measured);
        Assert.Null(unknown.Fact.CachedInputTokens);
        Assert.Null(unknown.Fact.CacheWriteInputTokens);
        Assert.Null(unknown.Fact.ReasoningOutputTokens);
        Assert.Equal(0, measured.Fact.CachedInputTokens);
        Assert.Equal(0, measured.Fact.CacheWriteInputTokens);
    }

    [Theory]
    [InlineData(300, "5m")]
    [InlineData(3600, "1h")]
    [InlineData(1800, "30m")]
    [InlineData(43200, "12h")]
    [InlineData(301, null)]
    [InlineData(0, null)]
    public void CacheLifetimeUsesOnlyExplicitSupportedDurations(long seconds, string? expected)
    {
        var record = SdkRecord();
        record["data"]!["usage"]!["cacheTtlSeconds"] = seconds;
        var state = SdkState();
        var result = CopilotParser.Parse(record.ToJsonString(), state);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Fact.CacheWriteLifetime);
        Assert.Equal(expected is null, state.Warnings.Count > 0);
        Assert.Equal(0, state.SkippedRecords);
    }

    [Fact]
    public void MissingCallIdentityAndTimestampCannotCreateBillableFacts()
    {
        var identity = SdkRecord();
        identity["data"]!["usage"]!["accounting"]!["usageId"] = null;
        identity["data"]!["usage"]!["accounting"]!["sequence"] = null;
        identity["data"]!["usage"]!["apiCallId"] = null;
        Assert.Null(CopilotParser.Parse(identity.ToJsonString(), SdkState()));

        foreach (var timestamp in new[] { "bad-date", "2026-11-21", "" })
        {
            var invalid = SdkRecord();
            invalid["timestamp"] = timestamp;
            Assert.Null(CopilotParser.Parse(invalid.ToJsonString(), SdkState()));
        }
    }

    [Fact]
    public void SessionMetadataRecordsOnlySessionAndParentIdentifiers()
    {
        var state = SdkState();
        Assert.Null(CopilotParser.Parse("""{"type":"session.start","data":{"sessionId":"child","parentSessionId":"parent","selectedModel":"picked-model","context":{"cwd":"secret-project"},"summary":"secret-title"}}""", state));
        var usage = SdkRecord();
        usage["data"]!["usage"]!["accounting"] = null;
        usage["data"]!["usage"]!["model"] = null;
        var result = CopilotParser.Parse(usage.ToJsonString(), state);

        Assert.NotNull(result);
        Assert.Equal("child", result.Fact.SessionId);
        Assert.Equal("parent", result.ParentSessionId);
        Assert.Null(result.Fact.Model);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session.usage_checkpoint")]
    [InlineData("session.shutdown")]
    [InlineData("session.usage_info")]
    [InlineData("assistant.usage")]
    [InlineData("user.message")]
    public void AggregateContextAndTranscriptEventsAreNeverCounted(string type)
    {
        var state = SdkState();
        var record = SdkRecord();
        record["type"] = type;

        Assert.Null(CopilotParser.Parse(record.ToJsonString(), state));
        Assert.False(state.HasUsageRecords);
        Assert.Equal(0, state.SkippedRecords);
    }

    [Fact]
    public void DebugCallsExposeResponseIdentityAndExplicitChildRelationship()
    {
        var state = DebugState();
        var record = DebugRecord();
        var first = CopilotParser.Parse(record.ToJsonString(), state);
        record["rIdx"] = 1;
        var restarted = CopilotParser.Parse(record.ToJsonString(), state);

        Assert.NotNull(first);
        Assert.NotNull(restarted);
        Assert.Equal("debug-child", first.Fact.SessionId);
        Assert.Equal("debug-parent", first.ParentSessionId);
        Assert.Equal("vscode", first.Fact.Client);
        Assert.Equal(100, first.Fact.InputTokens);
        Assert.Equal(20, first.Fact.CachedInputTokens);
        Assert.Null(first.Fact.CacheWriteInputTokens);
        Assert.Equal(["call-a"], first.CorrelationIds);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795276800000), first.Fact.Timestamp);
        Assert.NotEqual(first.Fact.Id, restarted.Fact.Id);
    }

    [Theory]
    [InlineData("v", "2")]
    [InlineData("rIdx", "-1")]
    [InlineData("rIdx", "1.5")]
    [InlineData("ts", "-1")]
    [InlineData("ts", "253402300800000")]
    [InlineData("spanId", "null")]
    public void InvalidDebugEnvelopeIsExcluded(string field, string value)
    {
        var record = DebugRecord();
        record[field] = JsonNode.Parse(value);
        var state = DebugState();

        Assert.Null(CopilotParser.Parse(record.ToJsonString(), state));
        Assert.Equal(1, state.SkippedRecords);
    }

    [Fact]
    public void MalformedRecordsProduceBoundedContentFreeWarnings()
    {
        var state = SdkState();
        for (var index = 0; index < 30; index++)
        {
            Assert.Null(CopilotParser.Parse("{private prompt text", state));
        }

        Assert.Null(CopilotParser.Parse("[]", state));
        Assert.Equal(31, state.SkippedRecords);
        Assert.Equal(2, state.Warnings.Count);
        Assert.DoesNotContain(state.Warnings, warning => warning.Contains("private prompt", StringComparison.Ordinal));
    }

    [Fact]
    public void SdkDiscoveryIncludesArchivesButExcludesUnrelatedJsonl()
    {
        using var fixture = new SourceFixture();
        fixture.Add("session-state/one/events.jsonl");
        fixture.Add("session-state/one/events.0.jsonl");
        fixture.Add("session-state/one/events.23.jsonl");
        fixture.Add("session-state/one/events.backup.jsonl");
        fixture.Add("session-state/one/transcript.jsonl");
        fixture.Add("session-state/two/events.jsonl");
        fixture.Add("session-state/two/tools/events.jsonl");

        var files = CopilotDiscovery.EnumerateFiles(new("visual-studio", fixture.Path));

        Assert.Equal(4, files.Count);
        Assert.All(files, file => Assert.Equal("visual-studio", file.Client));
        Assert.All(files, file => Assert.Equal(CopilotSourceKind.Sdk, file.Kind));
        Assert.Equal(3, files.Count(file => file.SessionId == "one"));
        Assert.Empty(CopilotDiscovery.EnumerateFiles(new("shared", fixture.Path, false)));
    }

    [Fact]
    public void DebugDiscoveryIncludesWorkspaceGlobalProfilesAndChildLogsOnlyInDebugStores()
    {
        using var fixture = new SourceFixture();
        fixture.Add("workspaceStorage/workspace/GitHub.copilot-chat/debug-logs/session/main.jsonl");
        fixture.Add("workspaceStorage/workspace/GitHub.copilot-chat/debug-logs/session/explore-child.jsonl");
        fixture.Add("workspaceStorage/workspace/chatSessions/transcript.jsonl");
        fixture.Add("workspaceStorage/workspace/GitHub.copilot-chat/debug-logs/session/attachments/prompt.jsonl");
        fixture.Add("globalStorage/github.copilot-chat/debug-logs/global/main.jsonl");
        fixture.Add("profiles/profile/globalStorage/github.copilot-chat/debug-logs/profile/main.jsonl");

        var files = CopilotDiscovery.EnumerateFiles(new("vscode", fixture.Path));

        Assert.Equal(4, files.Count);
        Assert.All(files, file => Assert.Equal("vscode", file.Client));
        Assert.All(files, file => Assert.Equal(CopilotSourceKind.VsCodeDebug, file.Kind));
        Assert.Equal(2, files.Count(file => file.SessionId == "session"));
        var debugRoot = System.IO.Path.Combine(fixture.Path, "workspaceStorage", "workspace", "GitHub.copilot-chat", "debug-logs");
        Assert.Equal(2, CopilotDiscovery.EnumerateFiles(new("vscode", debugRoot)).Count);
        Assert.Throws<DirectoryNotFoundException>(() => CopilotDiscovery.EnumerateFiles(new("vscode", System.IO.Path.Combine(fixture.Path, "missing"))));
    }

    private static CopilotParserState SdkState(string session = "source-session") =>
        new(new("events.jsonl", "shared", CopilotSourceKind.Sdk, session));

    private static CopilotParserState DebugState() =>
        new(new("main.jsonl", "vscode", CopilotSourceKind.VsCodeDebug, "debug-parent"));

    private static JsonObject SdkRecord() => JsonNode.Parse("""
        {"id":"event-a","type":"session.usage_record","timestamp":"2026-11-21T17:46:02.524Z",
         "data":{"usage":{"model":"claude-haiku-4.5","inputTokens":60607,"outputTokens":221,
           "cacheReadTokens":60210,"cacheWriteTokens":391,"apiCallId":"call-a","providerCallId":"provider-a",
           "accounting":{"sourceSessionId":"source-session","usageId":"call-a","sequence":2}}}}
        """)!.AsObject();

    private static JsonObject DebugRecord() => JsonNode.Parse("""
        {"v":1,"rIdx":0,"type":"llm_request","ts":1795276800000,"sid":"debug-child","spanId":"span-a",
         "attrs":{"model":"gpt-5.4","inputTokens":100,"outputTokens":10,"cachedTokens":20,"responseId":"call-a"}}
        """)!.AsObject();

    private sealed class SourceFixture : IDisposable
    {
        public SourceFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"copilot-parser-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Add(string relativePath)
        {
            var file = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}\n");
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
