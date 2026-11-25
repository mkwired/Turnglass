using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class AotSerializationTests
{
    private static readonly JsonSerializerOptions _legacyOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void CursorScopeContractsPreserveLegacyJsonBytes()
    {
        Assert.Equal("""{"query":null,"project":null,"model":null,"from":null,"to":null}""",
            JsonStorage.Serialize(new ViewerService.SessionCursorScope(null, null, null, null, null)));
        Assert.Equal("""{"sessionId":"cursor-session"}""", JsonStorage.Serialize(new ViewerService.EventCursorScope("cursor-session")));
        Assert.Equal("""{"query":"needle","sessionId":null}""", JsonStorage.Serialize(new ViewerService.SearchCursorScope("needle", null)));
        Assert.Equal("""{"project":"legacy-project","from":"2026-11-24T00:00:00+00:00","to":"2026-11-25T00:00:00+00:00"}""",
            JsonStorage.Serialize(new KnowledgeService.ReviewCursorScope("legacy-project", new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero))));
        Assert.Equal("""{"query":null,"project":null,"category":null,"status":null,"approvedOnly":false}""",
            JsonStorage.Serialize(new KnowledgeService.InsightCursorScope(null, null, null, null, false)));
    }

    [Fact]
    public void StorageUsesGeneratedContractsWithoutReflectionFallback()
    {
        Assert.Throws<NotSupportedException>(() => JsonStorage.Serialize(new UnregisteredRecord("must not reflect")));
        Assert.Throws<NotSupportedException>(() => JsonStorage.Deserialize<UnregisteredRecord>("""{"value":"must not reflect"}"""));
        Assert.Throws<InvalidDataException>(() => JsonStorage.Deserialize<ViewerSettings>("null"));
    }

    [Fact]
    public void StorageRetainsWebDefaultsNumericEnumsAndOptionalFieldCompatibility()
    {
        // Old records may omit fields added by later versions, and storage historically accepted quoted numbers.
        var tokens = JsonStorage.Deserialize<TokenUsage>("""{"InputTokens":"120","OutputTokens":"20"}""");
        Assert.Equal(new TokenUsage(120, OutputTokens: 20), tokens);
        Assert.Equal("""{"inputTokens":120,"cachedInputTokens":0,"cacheWriteInputTokens":0,"outputTokens":20,"reasoningOutputTokens":0,"totalTokens":140}""", JsonStorage.Serialize(tokens));

        const string legacyFact = """{"fact":{"id":"fact","sessionId":"session","timestamp":"2026-11-21T20:00:00+00:00","model":null,"client":"vscode","inputTokens":100,"outputTokens":10},"kind":1,"correlationIds":["response"]}""";
        var parsed = JsonStorage.Deserialize<CopilotParsedUsage>(legacyFact);
        Assert.Equal(CopilotSourceKind.VsCodeDebug, parsed.Kind);
        Assert.Null(parsed.Fact.CachedInputTokens);
        Assert.Null(parsed.AccountingSequence);
        Assert.False(parsed.HasResponseModel);
        using var serialized = JsonDocument.Parse(JsonStorage.Serialize(parsed));
        Assert.Equal(1, serialized.RootElement.GetProperty("kind").GetInt32());
        Assert.Equal(JsonValueKind.Null, serialized.RootElement.GetProperty("fact").GetProperty("cachedInputTokens").ValueKind);
    }

    [Fact]
    public void CoreContractsKeepDecimalPrecisionNullsAndComputedProperties()
    {
        var catalog = new PricingCatalog("custom <price>", "https://example.test/prices",
            [new PriceRate("model", "standard", 0, null, 0.1234567890123456789012345678m, 0.01m, 0.5m, 0.125m, null, 0.125m, 0.2m)],
            new Dictionary<string, string> { ["alias"] = "model" }, new Dictionary<string, string>(), new Dictionary<string, string> { ["model"] = "30m" });
        var settings = new ViewerSettings(["D:\\synthetic"], "America/New_York", catalog);
        var legacyJson = JsonSerializer.Serialize(settings, _legacyOptions);
        var generatedJson = JsonSerializer.Serialize(settings, TurnglassJsonContext.Default.ViewerSettings);
        Assert.Equal(legacyJson, generatedJson);
        var roundTrip = JsonSerializer.Deserialize(generatedJson, TurnglassJsonContext.Default.ViewerSettings);
        Assert.NotNull(roundTrip);
        Assert.Equal(catalog.Rates[0], Assert.Single(roundTrip.Pricing.Rates));

        var totals = new UsageTotals(new TokenUsage(100, 60, 10, 20, 5), 0.123456789m, 0, 0, []);
        Assert.Equal(JsonSerializer.Serialize(totals, _legacyOptions),
            JsonSerializer.Serialize(totals, TurnglassJsonContext.Default.UsageTotals));
    }

    [Fact]
    public async Task LegacySessionEventAndInsightCursorsRemainAccepted()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("cursor.jsonl", InfrastructureFixture.Meta("cursor-session"),
            InfrastructureFixture.Message("First", ordinal: 2), InfrastructureFixture.Message("Second", ordinal: 3)).ConfigureAwait(true);
        await fixture.WriteAsync("later.jsonl", InfrastructureFixture.Meta("later-session")).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        using (var connection = await fixture.Services.GetRequiredService<ViewerDatabase>().OpenAsync(CancellationToken).ConfigureAwait(true))
        {
            await ViewerDatabase.SetValueAsync(connection, "generation", "legacy-generation", CancellationToken).ConfigureAwait(true);
        }

        // Fixed JSON and hashes use the pre-source-generation wire representation, not the new serializer.
        const string eventCursor = """{"kind":"events","scope":"386593dc8af429ed3a703cb12d7b2ecf85854e0133cd9790e813f76d27d4b965","generation":"legacy-generation","id":"zzzz","timestamp":null,"ordinal":2}""";
        var events = await fixture.Viewer.ReadEventsAsync("cursor-session", Encode(eventCursor), cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Equal("Second", Assert.Single(events.Items).Text);

        const string sessionCursor = """{"kind":"sessions","scope":"b756e2927e3b94843fcd57776683d8caecd7b4726353636a4b4dadc32d2477db","generation":"legacy-generation","id":"cursor-session","timestamp":"2026-11-21T19:00:00+00:00","ordinal":null}""";
        var sessions = await fixture.Viewer.ListSessionsAsync(new SessionFilter(Cursor: Encode(sessionCursor)), CancellationToken).ConfigureAwait(true);
        Assert.Equal("later-session", Assert.Single(sessions.Items).Session.Id);

        var insight = new Insight("later-insight", "Keep this", "lesson", "global", null, null, "inferred", "Synthetic evidence", "draft", 1,
            InfrastructureFixture.Now, InfrastructureFixture.Now, [], [], []);
        using (var connection = await fixture.Services.GetRequiredService<KnowledgeDatabase>().OpenAsync(CancellationToken).ConfigureAwait(true))
        using (var transaction = connection.BeginTransaction())
        {
            await KnowledgeDatabase.SaveAsync(connection, transaction, "insights", insight.Id, insight, CancellationToken).ConfigureAwait(true);
            await transaction.CommitAsync(CancellationToken).ConfigureAwait(true);
        }

        const string insightCursor = """{"scope":"78ba9dd8951144f55525c068e87b84134ea6d3317cb941aeca9bd2022066104b","timestamp":"2026-11-21T20:00:00+00:00","id":"earlier-insight"}""";
        var insights = await fixture.Services.GetRequiredService<IKnowledgeService>().ListInsightsAsync(new InsightQuery(Cursor: Encode(insightCursor)), cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Equal(insight.Id, Assert.Single(insights.Items).Id);
    }

    private static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private sealed record UnregisteredRecord(string Value);
}
