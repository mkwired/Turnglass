using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class CopilotInfrastructureTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LedgerSurvivesDuplicateFilesRotationSourceRemovalRestartsAndCodexRebuilds()
    {
        using var fixture = new CopilotFixture();
        var path = await fixture.WriteSdkAsync("session-a", Sdk("session-a", "call-a"), new { type = "user.message", data = new { content = "private transcript must not be retained" } }).ConfigureAwait(true);
        var duplicate = Path.Combine(Path.GetDirectoryName(path)!, "events.1.jsonl");
        File.Copy(path, duplicate);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var first = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal(110, first.Periods[0].Totals.Tokens.TotalTokens);
        Assert.Equal(1, first.Index.RecordCount);
        Assert.True(first.Index.InitialScanComplete);
        File.Delete(path);
        File.Delete(duplicate);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var rotated = await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, rotated.RecordCount);
        Assert.True(rotated.Sources.Single(source => source.Kind == "visual-studio").Available);
        Assert.Equal(0, rotated.Sources.Single(source => source.Kind == "visual-studio").FileCount);
        await fixture.Copilot.UpdateSettingsAsync(new CopilotSettings(false, []), CancellationToken).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        Assert.All((await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).Sources, source => Assert.False(source.Enabled));
        await fixture.Viewer.RebuildAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(0, (await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).SessionCount);
        using var restarted = fixture.CreateServices();
        var service = restarted.GetRequiredService<ICopilotUsageService>();
        Assert.Equal(1, (await service.GetUsageAsync(new CopilotUsageQuery(), CancellationToken).ConfigureAwait(true)).MatchingRecordCount);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Data, "copilot-usage.db")}");
        await connection.OpenAsync(CancellationToken).ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM facts";
        var stored = Assert.IsType<string>(await command.ExecuteScalarAsync(CancellationToken).ConfigureAwait(true));
        Assert.DoesNotContain("private transcript", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialWritesAppendAndReplacementRetainExactlyOnceUsage()
    {
        using var fixture = new CopilotFixture();
        var path = await fixture.WriteSdkAsync("append", Sdk("append", "first")).ConfigureAwait(true);
        var first = await File.ReadAllTextAsync(path, CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, first.TrimEnd('\n'), CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(0, (await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).RecordCount);
        await File.AppendAllTextAsync(path, "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        await File.AppendAllTextAsync(path, Serialize(Sdk("append", "second")) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, (await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).RecordCount);
        await File.WriteAllTextAsync(path, Serialize(Sdk("append", "replacement")) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(3, (await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).RecordCount);
    }

    [Fact]
    public async Task SharedSdkCorrelationAndSessionFamilyAuthorityPreventDoubleCounting()
    {
        using var fixture = new CopilotFixture();
        await fixture.WriteSdkAsync("sdk-session", Sdk("sdk-session", "provider-response"), shared: true).ConfigureAwait(true);
        await fixture.WriteDebugAsync("debug-session", "main.jsonl", Debug("debug-session", "span", "provider-response")).ConfigureAwait(true);
        await fixture.WriteSdkAsync("family", Sdk("family", "family-sdk")).ConfigureAwait(true);
        await fixture.WriteDebugAsync("family", "main.jsonl", Debug("family", "other-span", "uncorrelated-response")).ConfigureAwait(true);
        await fixture.WriteDebugAsync("family", "child.jsonl", Debug("child", "child-span", "child-response")).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var report = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, report.MatchingRecordCount);
        Assert.Equal(220, report.Periods[0].Totals.Tokens.TotalTokens);
        Assert.Equal(2, report.Index.AmbiguousRecords);
        Assert.Contains(report.Index.Warnings, warning => warning.Contains("session families", StringComparison.Ordinal));
        var vscode = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(Client: "vscode"), CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, vscode.MatchingRecordCount);
        Assert.Equal(0, (await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(Client: "shared"), CancellationToken).ConfigureAwait(true)).MatchingRecordCount);
    }

    [Fact]
    public async Task UncorrelatedSharedRecordsRemainExplicitAndInvalidRecordsDoNotBecomeZeroUsage()
    {
        using var fixture = new CopilotFixture();
        await fixture.WriteSdkAsync("shared", Sdk("shared", "shared-call"), "{broken", Sdk("shared", "invalid", input: -1), shared: true).ConfigureAwait(true);
        await fixture.WriteDebugAsync("separate", "main.jsonl", Debug("separate", "span", "other-response", model: "unknown-model")).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var report = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, report.Index.RecordCount);
        Assert.Equal(2, report.Index.SkippedRecords);
        Assert.Contains(report.Index.Warnings, warning => warning.Contains("may overlap", StringComparison.Ordinal));
        Assert.Equal(1, report.UnknownCacheWriteRecords);
        Assert.False(report.Periods[0].Totals.IsPricingComplete);
        Assert.Equal(110, report.Periods[0].Totals.UnpricedTokens);
    }

    [Fact]
    public async Task SourceFailureRetainsHistoryAndPreviousScanTimeWithoutAffectingCodex()
    {
        using var fixture = new CopilotFixture();
        await fixture.WriteSdkAsync("history", Sdk("history", "call")).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var prior = await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true);
        var missing = Path.Combine(fixture.Root, "missing");
        await fixture.Copilot.UpdateSettingsAsync(new CopilotSettings(false, [new CopilotSourceRoot("visual-studio", missing)]), CancellationToken).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var status = await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, status.RecordCount);
        Assert.True(status.InitialScanComplete);
        Assert.Equal(prior.LastSuccessfulScanUtc, status.LastSuccessfulScanUtc);
        Assert.False(status.Sources.Single(source => source.Enabled).Available);
        Assert.NotEmpty(status.Warnings);
        await fixture.Viewer.RebuildAsync(CancellationToken).ConfigureAwait(true);
        Assert.True((await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).IsUsageComplete);
    }

    [Fact]
    public async Task FiltersPeriodsAndEditableSharedPricesApplyToRetainedFacts()
    {
        using var fixture = new CopilotFixture();
        await fixture.WriteSdkAsync("old", Sdk("old", "old-call", timestamp: CopilotFixture.Now.AddDays(-3))).ConfigureAwait(true);
        await fixture.WriteSdkAsync("new", Sdk("new", "new-call")).ConfigureAwait(true);
        await fixture.WriteDebugAsync("debug", "main.jsonl", Debug("debug", "span", "response", model: "custom-model")).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var filters = await fixture.Copilot.GetFiltersAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(3, filters.Clients.Count);
        Assert.Contains("custom-model", filters.Models);
        var report = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery("rolling", "UTC", "visual-studio", From: CopilotFixture.Now.AddDays(-4), To: CopilotFixture.Now), CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, report.MatchingRecordCount);
        Assert.Equal(110, report.Periods[0].Totals.Tokens.TotalTokens);
        Assert.Equal(220, report.Periods[1].Totals.Tokens.TotalTokens);
        Assert.Equal(5, report.Buckets.Count);
        Assert.Equal(220, report.Buckets.Sum(bucket => bucket.Totals.Tokens.TotalTokens));
        var settings = await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        var pricing = settings.Pricing with { Rates = [new PriceRate("custom-model", "standard", 0, null, 10m, 1m, 50m)] };
        await fixture.Viewer.UpdateSettingsAsync(settings with { Pricing = pricing, Timezone = "UTC" }, CancellationToken).ConfigureAwait(true);
        var repriced = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(Model: "custom-model"), CancellationToken).ConfigureAwait(true);
        Assert.Equal("UTC", repriced.Timezone);
        Assert.True(repriced.Periods[0].Totals.IsPricingComplete);
        Assert.True(repriced.Periods[0].Totals.KnownCostUsd > 0m);
        Assert.NotEmpty(repriced.PricingAssumptions);
        Assert.Equal(1, repriced.UnknownCacheWriteRecords);
    }

    [Fact]
    public async Task SameIdentityEnrichesMetadataButConflictingCountsRemainVisible()
    {
        using var fixture = new CopilotFixture();
        var path = await fixture.WriteSdkAsync("enrich", Sdk("enrich", "call", model: null)).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, Serialize(Sdk("enrich", "call")) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        Assert.Contains("gpt-6-astra", (await fixture.Copilot.GetFiltersAsync(CancellationToken).ConfigureAwait(true)).Models);
        await File.WriteAllTextAsync(path, Serialize(Sdk("enrich", "call", input: 200)) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var report = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal(110, report.Periods[0].Totals.Tokens.TotalTokens);
        Assert.Contains(report.Index.Warnings, warning => warning.Contains("Conflicting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NullableCacheEnrichmentCannotCreateAnImpossibleBreakdown()
    {
        using var fixture = new CopilotFixture();
        var path = await fixture.WriteSdkAsync("cache", Sdk("cache", "call", cached: 90, written: null)).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, Serialize(Sdk("cache", "call", cached: null, written: 30)) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var report = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal(90, report.Periods[0].Totals.Tokens.CachedInputTokens);
        Assert.Equal(1, report.UnknownCacheWriteRecords);
        Assert.Contains(report.Index.Warnings, warning => warning.Contains("cache breakdowns", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogicalAliasesAndScopedSequencesDeduplicateRicherCopiesAndRepeatedSpans()
    {
        using var fixture = new CopilotFixture();
        static object Usage(string? usageId, string? apiCallId, long? sequence, string? responseModel = null) => new
        {
            type = "session.usage_record",
            timestamp = CopilotFixture.Now.AddHours(-1),
            data = new { usage = new { model = "requested-model", responseModel, inputTokens = 100, outputTokens = 10, apiCallId, accounting = new { sourceSessionId = "aliases", usageId, sequence } } },
        };
        await fixture.WriteSdkAsync("aliases", Usage(null, null, 1), Usage(null, "api-call", 1), Usage("usage-id", "api-call", 1, "gpt-6-astra")).ConfigureAwait(true);
        await fixture.WriteDebugAsync("independent", "main.jsonl", Debug("independent", "span-a", "same-response"), Debug("independent", "span-b", "same-response")).ConfigureAwait(true);
        await fixture.Copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
        var report = await fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, report.Index.RecordCount);
        Assert.Equal(220, report.Periods[0].Totals.Tokens.TotalTokens);
        var models = (await fixture.Copilot.GetFiltersAsync(CancellationToken).ConfigureAwait(true)).Models;
        Assert.DoesNotContain("requested-model", models);
        Assert.Contains("gpt-6-astra", models);
    }

    [Fact]
    public async Task SettingsValidateSourceKindsPathsAndQueriesWithoutChangingCodexSettings()
    {
        using var fixture = new CopilotFixture();
        var codex = await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        var original = await fixture.Copilot.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        Assert.False(original.AutoDiscover);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Copilot.UpdateSettingsAsync(new CopilotSettings(false, [new CopilotSourceRoot("unknown", fixture.Root)]), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Copilot.UpdateSettingsAsync(new CopilotSettings(false, [new CopilotSourceRoot("vscode", "relative")]), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(Client: "other"), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(From: CopilotFixture.Now, To: CopilotFixture.Now), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Copilot.GetUsageAsync(new CopilotUsageQuery(From: CopilotFixture.Now.AddYears(-20)), CancellationToken)).ConfigureAwait(true);
        var normalized = await fixture.Copilot.UpdateSettingsAsync(new CopilotSettings(false, [original.Sources[0], original.Sources[0]]), CancellationToken).ConfigureAwait(true);
        Assert.Single(normalized.Sources);
        Assert.Equal(Serialize(codex), Serialize(await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true)));
    }

    [Fact]
    public async Task BackgroundWorkerCollectsAndCompetingScanDoesNotEraseHistory()
    {
        using var fixture = new CopilotFixture();
        await fixture.WriteSdkAsync("worker", Sdk("worker", "call")).ConfigureAwait(true);
        using var worker = new CopilotIndexingWorker(fixture.Indexer, NullLogger<CopilotIndexingWorker>.Instance);
        await worker.StartAsync(CancellationToken).ConfigureAwait(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await fixture.Copilot.GetIndexStatusAsync(timeout.Token).ConfigureAwait(true)).RecordCount == 0)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }

        await worker.StopAsync(CancellationToken).ConfigureAwait(true);
        using var other = fixture.CreateServices();
        using var initialized = await other.GetRequiredService<CopilotDatabase>().OpenAsync(CancellationToken).ConfigureAwait(true);
        using (var held = fixture.Services.GetRequiredService<CopilotDatabase>().TryAcquireLock())
        {
            Assert.NotNull(held);
            await other.GetRequiredService<CopilotIndexer>().RefreshAsync(CancellationToken).ConfigureAwait(true);
        }

        Assert.Equal(1, (await fixture.Copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).RecordCount);
    }

    [Fact]
    public async Task NewerLedgerSchemaIsRejectedWithoutChangingCodexDatabase()
    {
        using var fixture = new CopilotFixture();
        await fixture.Copilot.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Data, "copilot-usage.db")}"))
        {
            await connection.OpenAsync(CancellationToken).ConfigureAwait(true);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            await command.ExecuteNonQueryAsync(CancellationToken).ConfigureAwait(true);
        }

        using var other = fixture.CreateServices();
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.GetRequiredService<ICopilotUsageService>().GetSettingsAsync(CancellationToken)).ConfigureAwait(true);
        Assert.NotNull(await other.GetRequiredService<IViewerService>().GetSettingsAsync(CancellationToken).ConfigureAwait(true));
    }

    private static object Sdk(string session, string id, long input = 100, string? model = "gpt-6-astra", DateTimeOffset? timestamp = null, long? cached = 20, long? written = 0) => new
    {
        type = "session.usage_record",
        timestamp = timestamp ?? CopilotFixture.Now.AddHours(-1),
        data = new { usage = new { model, inputTokens = input, outputTokens = 10, cacheReadTokens = cached, cacheWriteTokens = written, reasoningTokens = 2, cost = 1000m, accounting = new { sourceSessionId = session, usageId = id } } },
    };

    private static object Debug(string session, string span, string response, string model = "gpt-6-astra") => new
    {
        v = 1,
        type = "llm_request",
        sid = session,
        spanId = span,
        rIdx = 0,
        ts = CopilotFixture.Now.AddHours(-1).ToUnixTimeMilliseconds(),
        attrs = new { responseId = response, model, inputTokens = 100, outputTokens = 10, cachedTokens = 20 },
    };

    private static string Serialize(object value) => value is string raw ? raw : JsonSerializer.Serialize(value);

    private sealed class CopilotFixture : IDisposable
    {
        internal static DateTimeOffset Now { get; } = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Turnglass.Tests", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        private string VisualStudio => Path.Combine(Root, "vs");
        private string Shared => Path.Combine(Root, "shared");
        private string DebugLogs => Path.Combine(Root, "debug-logs");
        internal ServiceProvider Services { get; }
        internal ICopilotUsageService Copilot => Services.GetRequiredService<ICopilotUsageService>();
        internal IViewerService Viewer => Services.GetRequiredService<IViewerService>();
        internal CopilotIndexer Indexer => Services.GetRequiredService<CopilotIndexer>();

        internal CopilotFixture()
        {
            Directory.CreateDirectory(VisualStudio);
            Directory.CreateDirectory(Shared);
            Directory.CreateDirectory(DebugLogs);
            Services = CreateServices();
        }

        internal ServiceProvider CreateServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(new FixtureClock());
            services.AddTurnglass(new ViewerOptions
            {
                DataDirectory = Data,
                Sources = [],
                CopilotSources = [new("visual-studio", VisualStudio), new("shared", Shared), new("vscode", DebugLogs)],
            });
            return services.BuildServiceProvider();
        }

        internal Task<string> WriteSdkAsync(string session, object record, bool shared = false) => WriteSdkAsync(session, [record], shared);

        internal Task<string> WriteSdkAsync(string session, object first, object second, bool shared = false) => WriteSdkAsync(session, [first, second], shared);

        internal Task<string> WriteSdkAsync(string session, object first, object second, object third, bool shared = false) => WriteSdkAsync(session, [first, second, third], shared);

        private async Task<string> WriteSdkAsync(string session, object[] records, bool shared)
        {
            var directory = Path.Combine(shared ? Shared : VisualStudio, session);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "events.jsonl");
            await File.WriteAllTextAsync(path, string.Join('\n', records.Select(Serialize)) + "\n", CancellationToken).ConfigureAwait(true);
            return path;
        }

        internal async Task WriteDebugAsync(string session, string file, params object[] records)
        {
            var directory = Path.Combine(DebugLogs, session);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, file), string.Join('\n', records.Select(Serialize)) + "\n", CancellationToken).ConfigureAwait(true);
        }

        public void Dispose()
        {
            Services.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }

        private sealed class FixtureClock : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
