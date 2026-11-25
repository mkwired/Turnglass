using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

[CollectionDefinition("Turnglass configuration", DisableParallelization = true)]
public sealed class TurnglassConfigurationTestsDefinition;

[Collection("Turnglass configuration")]
public sealed class TurnglassCompatibilityTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExplicitDirectoryWinsOverBothEnvironmentNamesAndWritesOnlyThere()
    {
        using var scope = new ConfigurationScope();
        var explicitDirectory = Path.Combine(scope.Root, "explicit");
        var environmentDirectory = Path.Combine(scope.Root, "new-variable");
        var legacyDirectory = Path.Combine(scope.Root, "legacy-variable");
        scope.Set("TURNGLASS_DATA_DIR", environmentDirectory);
        scope.Set("CODEX_SESSION_VIEWER_DATA_DIR", legacyDirectory);
        using var services = CreateServices(explicitDirectory);
        await services.GetRequiredService<IViewerService>().GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(explicitDirectory, services.GetRequiredService<ViewerOptions>().DataDirectory);
        Assert.True(File.Exists(Path.Combine(explicitDirectory, "session-viewer.db")));
        Assert.False(Directory.Exists(environmentDirectory));
        Assert.False(Directory.Exists(legacyDirectory));
    }

    [Fact]
    public async Task TurnglassVariableSelectsStorageAndLegacyVariableAloneDoesNotRedirectTheDefault()
    {
        using var scope = new ConfigurationScope();
        var selected = Path.Combine(scope.Root, "selected");
        var legacy = Path.Combine(scope.Root, "legacy");
        scope.Set("TURNGLASS_DATA_DIR", selected);
        scope.Set("CODEX_SESSION_VIEWER_DATA_DIR", legacy);
        using (var services = CreateServices())
        {
            await services.GetRequiredService<IViewerService>().GetSettingsAsync(CancellationToken).ConfigureAwait(true);
            Assert.Equal(selected, services.GetRequiredService<ViewerOptions>().DataDirectory);
        }

        Assert.True(File.Exists(Path.Combine(selected, "session-viewer.db")));
        Assert.False(Directory.Exists(legacy));
        scope.Set("TURNGLASS_DATA_DIR", null);
        using var defaults = CreateServices();
        // Resolve configuration only: the actual per-user default directory must never be opened by a test.
        var defaultPath = defaults.GetRequiredService<ViewerOptions>().DataDirectory;
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Turnglass"), defaultPath);
        Assert.NotEqual(legacy, defaultPath);
    }

    [Fact]
    public async Task OnlyNewCopilotVariableControlsFirstInitializationAndSavedSettingsTakePrecedence()
    {
        using var scope = new ConfigurationScope();
        scope.Set("TURNGLASS_COPILOT_AUTO_DISCOVER", null);
        scope.Set("CODEX_SESSION_VIEWER_COPILOT_AUTO_DISCOVER", "false");
        using (var legacyOnly = CreateServices(Path.Combine(scope.Root, "legacy-only")))
        {
            var settings = await legacyOnly.GetRequiredService<ICopilotUsageService>().GetSettingsAsync(CancellationToken).ConfigureAwait(true);
            Assert.True(settings.AutoDiscover);
        }

        scope.Set("TURNGLASS_COPILOT_AUTO_DISCOVER", "false");
        scope.Set("CODEX_SESSION_VIEWER_COPILOT_AUTO_DISCOVER", "true");
        var data = Path.Combine(scope.Root, "saved-choice");
        using (var selected = CreateServices(data))
        {
            var copilot = selected.GetRequiredService<ICopilotUsageService>();
            Assert.False((await copilot.GetSettingsAsync(CancellationToken).ConfigureAwait(true)).AutoDiscover);
            await copilot.UpdateSettingsAsync(new CopilotSettings(true, []), CancellationToken).ConfigureAwait(true);
        }

        using var restarted = CreateServices(data);
        Assert.True((await restarted.GetRequiredService<ICopilotUsageService>().GetSettingsAsync(CancellationToken).ConfigureAwait(true)).AutoDiscover);
    }

    [Fact]
    public async Task ExplicitDirectoryReopensOldAuthoredSettingsInsightsAndRetainedCopilotUsage()
    {
        using var scope = new ConfigurationScope();
        var oldData = Path.Combine(scope.Root, "pre-rename-data");
        Directory.CreateDirectory(oldData);
        foreach (var database in new[] { "session-viewer.db", "knowledge.db", "copilot-usage.db" })
        {
            await RestoreFixtureAsync(database, oldData).ConfigureAwait(true);
        }

        var unusedDefault = Path.Combine(scope.Root, "other-data");
        scope.Set("TURNGLASS_DATA_DIR", unusedDefault);
        using (var services = CreateServices(oldData))
        {
            await AssertPreRenameJsonAsync(services).ConfigureAwait(true);
            var viewer = services.GetRequiredService<IViewerService>();
            var settings = await viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
            Assert.Equal("UTC", settings.Timezone);
            Assert.Equal("Synthetic legacy custom prices", settings.Pricing.Version);
            var rate = Assert.Single(settings.Pricing.Rates);
            Assert.Equal("legacy-model", rate.Model);
            Assert.Equal(3m, rate.InputPerMillion);
            Assert.Equal(0.3m, rate.CachedInputPerMillion);
            Assert.Equal(15m, rate.OutputPerMillion);
            var session = await viewer.GetSessionAsync("legacy-session", CancellationToken).ConfigureAwait(true);
            Assert.NotNull(session);
            Assert.Equal("Synthetic pre-rename session", session.Detail.Session.Title);
            Assert.Equal(140, session.Detail.OwnUsage.Tokens.TotalTokens);
            Assert.Equal(0.000606m, session.Detail.OwnUsage.KnownCostUsd);
            Assert.Contains((await viewer.ReadEventsAsync("legacy-session", cancellationToken: CancellationToken).ConfigureAwait(true)).Items,
                item => item.Role == "user" && item.Text == "Keep compatibility tests focused.");

            var knowledge = services.GetRequiredService<IKnowledgeService>();
            var insight = Assert.Single((await knowledge.ListInsightsAsync(new InsightQuery(Project: "legacy-project"), approvedOnly: true, cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
            Assert.Equal("Keep compatibility tests focused.", insight.Statement);
            Assert.Equal("approved", insight.Status);
            Assert.Equal(2, insight.Revision);
            Assert.Equal("available", Assert.Single(insight.Evidence).Availability);
            Assert.Equal(["created", "approve"], insight.History.Select(revision => revision.Action));
            var progress = await knowledge.GetReviewProgressAsync(CancellationToken).ConfigureAwait(true);
            Assert.Equal(1, progress.Approved);
            Assert.Equal(1, progress.ReviewedSessions);
            Assert.True(progress.ReviewedCharacters > 0);

            var originalDraft = new InsightDraft(insight.Statement, insight.Category, insight.Scope, insight.Project, insight.SessionId, insight.Basis,
                insight.Rationale, [.. insight.Evidence.Select(item => new EvidenceInput(item.SessionId, item.EventId, item.Quote))]);
            var replay = await knowledge.SubmitReviewAsync(new ReviewSubmission("17fdc5d23fe94676907a8cdd6dcd3a4b", "synthetic-legacy-review", [originalDraft]), CancellationToken).ConfigureAwait(true);
            Assert.True(replay.Replayed);
            Assert.Equal(insight.Id, Assert.Single(replay.InsightIds));
            var continuation = await knowledge.GetReviewPacketAsync(new ReviewQuery("legacy-project", new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero), "17fdc5d23fe94676907a8cdd6dcd3a4b"), CancellationToken).ConfigureAwait(true);
            Assert.Null(continuation.PacketId);

            var copilot = services.GetRequiredService<ICopilotUsageService>();
            var copilotSettings = await copilot.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
            Assert.False(copilotSettings.AutoDiscover);
            Assert.Equal("visual-studio", Assert.Single(copilotSettings.Sources).Kind);
            var usage = await copilot.GetUsageAsync(new CopilotUsageQuery(Client: "visual-studio"), CancellationToken).ConfigureAwait(true);
            Assert.Equal(1, usage.MatchingRecordCount);
            Assert.Equal(330, usage.Periods[0].Totals.Tokens.TotalTokens);
            Assert.Equal(0.001215m, usage.Periods[0].Totals.KnownCostUsd);
            Assert.True(usage.Periods[0].Totals.IsPricingComplete);
            // Do not access the fixture's historical source paths. Retention must also work with collection disabled.
            await copilot.UpdateSettingsAsync(new CopilotSettings(false, []), CancellationToken).ConfigureAwait(true);
            await copilot.RescanAsync(CancellationToken).ConfigureAwait(true);
            Assert.Equal(1, (await copilot.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).RecordCount);
        }

        using var reopened = CreateServices(oldData);
        Assert.Single((await reopened.GetRequiredService<IKnowledgeService>().ListInsightsAsync(new InsightQuery(Project: "legacy-project"), approvedOnly: true, cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
        Assert.Equal(1, (await reopened.GetRequiredService<ICopilotUsageService>().GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).RecordCount);
        Assert.False(Directory.Exists(unusedDefault));
    }

    private static ServiceProvider CreateServices(string? directory = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixtureClock());
        services.AddTurnglass(directory);
        return services.BuildServiceProvider();
    }

    private static async Task AssertPreRenameJsonAsync(ServiceProvider services)
    {
        using var viewer = await services.GetRequiredService<ViewerDatabase>().OpenAsync(CancellationToken).ConfigureAwait(true);
        await AssertStoredRowsAsync<ViewerSettings>(viewer, "SELECT value FROM configuration WHERE key='settings'").ConfigureAwait(true);
        await AssertStoredRowsAsync<IndexStatus>(viewer, "SELECT value FROM configuration WHERE key='index'").ConfigureAwait(true);
        await AssertStoredRowsAsync<ParserState>(viewer, "SELECT state FROM files").ConfigureAwait(true);
        await AssertStoredRowsAsync<UsageFact>(viewer, "SELECT fact FROM usage").ConfigureAwait(true);
        using var knowledge = await services.GetRequiredService<KnowledgeDatabase>().OpenAsync(CancellationToken).ConfigureAwait(true);
        await AssertStoredRowsAsync<Insight>(knowledge, "SELECT value FROM insights UNION ALL SELECT value FROM revisions").ConfigureAwait(true);
        await AssertStoredRowsAsync<KnowledgeService.Checkpoint>(knowledge, "SELECT value FROM checkpoints").ConfigureAwait(true);
        await AssertStoredRowsAsync<KnowledgeService.SavedPacket>(knowledge, "SELECT value FROM packets").ConfigureAwait(true);
        await AssertStoredRowsAsync<KnowledgeService.SavedSubmission>(knowledge, "SELECT value FROM submissions").ConfigureAwait(true);
        using var copilot = await services.GetRequiredService<CopilotDatabase>().OpenAsync(CancellationToken).ConfigureAwait(true);
        await AssertStoredRowsAsync<CopilotSettings>(copilot, "SELECT value FROM configuration WHERE id='settings'").ConfigureAwait(true);
        await AssertStoredRowsAsync<CopilotIndexStatus>(copilot, "SELECT value FROM configuration WHERE id='index'").ConfigureAwait(true);
        await AssertStoredRowsAsync<CopilotParsedUsage>(copilot, "SELECT value FROM facts").ConfigureAwait(true);
        await AssertStoredRowsAsync<CopilotSourceStatus>(copilot, "SELECT value FROM sources").ConfigureAwait(true);
        await AssertStoredRowsAsync<CopilotIndexer.CopilotFileCheckpoint>(copilot, "SELECT value FROM checkpoints").ConfigureAwait(true);
    }

    private static async Task AssertStoredRowsAsync<T>(SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = await command.ExecuteReaderAsync(CancellationToken).ConfigureAwait(true);
        var count = 0;
        while (await reader.ReadAsync(CancellationToken).ConfigureAwait(true))
        {
            var oldJson = reader.GetString(0);
            Assert.Equal(oldJson, JsonStorage.Serialize(JsonStorage.Deserialize<T>(oldJson)));
            count++;
        }

        Assert.True(count > 0, $"The historical fixture must exercise {typeof(T).Name}.");
    }

    private static async Task RestoreFixtureAsync(string database, string directory)
    {
        var resource = $"Turnglass.Tests.Fixtures.PreRename.{database}.gz";
        using var compressed = typeof(TurnglassCompatibilityTests).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(compressed);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = File.Create(Path.Combine(directory, database));
        await gzip.CopyToAsync(output, CancellationToken).ConfigureAwait(true);
    }

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-24T20:00:00Z", CultureInfo.InvariantCulture);
    }

    private sealed class ConfigurationScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = [];

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Turnglass-compatibility-" + Guid.NewGuid().ToString("N"));

        internal ConfigurationScope()
        {
            Directory.CreateDirectory(Root);
            Set("CODEX_HOME", Path.Combine(Root, "codex"));
            Set("TURNGLASS_DATA_DIR", null);
            Set("CODEX_SESSION_VIEWER_DATA_DIR", null);
            Set("TURNGLASS_COPILOT_AUTO_DISCOVER", "false");
            Set("CODEX_SESSION_VIEWER_COPILOT_AUTO_DISCOVER", null);
        }

        internal void Set(string name, string? value)
        {
            _original.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }
}
