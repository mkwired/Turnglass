using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class InfrastructureTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModernRecordsAreDeduplicatedAcrossCountersFilesAndRestart()
    {
        using var fixture = new InfrastructureFixture();
        var initial = await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true);
        Assert.False(initial.InitialIndexComplete);
        var path = await fixture.WriteAsync("root.jsonl", InfrastructureFixture.Meta("root"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("root"), InfrastructureFixture.Usage("root"), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100)), InfrastructureFixture.Usage("root"), InfrastructureFixture.Record("compacted", new { latest_token_usage_record = new { usage = InfrastructureFixture.Tokens(100) } }, 7), InfrastructureFixture.Message("Build the telescope")).ConfigureAwait(true);
        File.Copy(path, Path.Combine(fixture.Archive, "copy.jsonl"));
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        using var second = fixture.CreateServices();
        await second.GetRequiredService<SessionIndexer>().RefreshAsync(CancellationToken).ConfigureAwait(true);
        var session = await second.GetRequiredService<IViewerService>().GetSessionAsync("root", CancellationToken).ConfigureAwait(true);
        Assert.NotNull(session);
        Assert.Equal(110, session.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.Equal("Build the telescope", session.Detail.Session.Title);
        Assert.True(session.Detail.OwnUsage.IsPricingComplete);
        Assert.True(session.Index.InitialIndexComplete);
        Assert.Equal(1, session.Index.SessionCount);
    }

    [Fact]
    public async Task ChildHistoryIsPreservedWithoutCountingParentUsage()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("root.jsonl", InfrastructureFixture.Meta("root"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("root"), InfrastructureFixture.Usage("root")).ConfigureAwait(true);
        await fixture.WriteAsync("child.jsonl", InfrastructureFixture.Meta("child", "root", 5), InfrastructureFixture.Meta("root"), InfrastructureFixture.Context(ordinal: 3), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100), ordinal: 4), InfrastructureFixture.Settings("child", ordinal: 5), InfrastructureFixture.Usage("child", "child-response", 20, 2, 6, InfrastructureFixture.Tokens(120, 12, 40, 4))).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var root = await fixture.Viewer.GetSessionAsync("root", CancellationToken).ConfigureAwait(true);
        var child = await fixture.Viewer.GetSessionAsync("child", CancellationToken).ConfigureAwait(true);
        Assert.NotNull(root);
        Assert.NotNull(child);
        Assert.Equal(110, root.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.Equal(132, root.Detail.FamilyUsage.Tokens.TotalTokens);
        Assert.Equal(22, child.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.Equal("root", child.Detail.Session.ParentId);
        Assert.Single(root.Children);
        var events = await fixture.Viewer.ReadEventsAsync("child", cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Contains(events.Items, item => item.Inherited);
    }

    [Fact]
    public async Task ModernForkOwnershipDoesNotRequireAnInheritedCounterBaseline()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("fork.jsonl", InfrastructureFixture.Meta("fork", "parent"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("fork"), InfrastructureFixture.Usage("fork"), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100))).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var result = await fixture.Viewer.GetSessionAsync("fork", CancellationToken).ConfigureAwait(true);
        Assert.NotNull(result);
        Assert.Equal(110, result.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.True(result.Index.IsUsageComplete);
        Assert.Empty(result.Index.Warnings);
    }

    [Fact]
    public async Task LegacyCountersAndModernCutoverKeepOnlyRealIncrements()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("legacy.jsonl", InfrastructureFixture.Meta("legacy"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("legacy"), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100)), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100)), InfrastructureFixture.Usage("legacy", "cutover", cumulative: InfrastructureFixture.Tokens(100)), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(200, 20, 40, 4)), InfrastructureFixture.Usage("legacy", "next", ordinal: 9)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var result = await fixture.Viewer.GetSessionAsync("legacy", CancellationToken).ConfigureAwait(true);
        Assert.NotNull(result);
        Assert.Equal(220, result.Detail.OwnUsage.Tokens.TotalTokens);
    }

    [Fact]
    public async Task LegacyOnlyDeltasHandleRepeatsResetsAndAmbiguousForks()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("legacy.jsonl", InfrastructureFixture.Meta("legacy"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("legacy"), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100)), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100)), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(200, 20, 40, 4), InfrastructureFixture.Tokens(100)), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(10, 1, 0, 0))).ConfigureAwait(true);
        await fixture.WriteAsync("fork.jsonl", InfrastructureFixture.Meta("fork", "parent"), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100))).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var legacy = await fixture.Viewer.GetSessionAsync("legacy", CancellationToken).ConfigureAwait(true);
        var fork = await fixture.Viewer.GetSessionAsync("fork", CancellationToken).ConfigureAwait(true);
        Assert.NotNull(legacy);
        Assert.NotNull(fork);
        Assert.Equal(220, legacy.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.Equal(0, fork.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.Contains(legacy.Index.Warnings, warning => warning.Contains("reset", StringComparison.Ordinal));
        Assert.Contains(fork.Index.Warnings, warning => warning.Contains("inherited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialWritesResumeWithPersistedModelAndUsageState()
    {
        using var fixture = new InfrastructureFixture();
        var path = await fixture.WriteAsync("live.jsonl", InfrastructureFixture.Meta("live"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("live")).ConfigureAwait(true);
        var usage = InfrastructureFixture.Serialize(InfrastructureFixture.Usage("live"));
        await File.AppendAllTextAsync(path, usage[..30], CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(0, (await fixture.Viewer.GetSessionAsync("live", CancellationToken).ConfigureAwait(true))!.Detail.OwnUsage.Tokens.TotalTokens);
        await File.AppendAllTextAsync(path, usage[30..] + "\n", CancellationToken).ConfigureAwait(true);
        using var other = fixture.CreateServices();
        await other.GetRequiredService<SessionIndexer>().RefreshAsync(CancellationToken).ConfigureAwait(true);
        var updated = await fixture.Viewer.GetSessionAsync("live", CancellationToken).ConfigureAwait(true);
        Assert.Equal(110, updated!.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.True(updated.Detail.OwnUsage.IsPricingComplete);
    }

    [Fact]
    public async Task TruncationArchiveMovesAndSourceRemovalReconcileSafely()
    {
        using var fixture = new InfrastructureFixture();
        var path = await fixture.WriteAsync("changing.jsonl", InfrastructureFixture.Meta("original"), InfrastructureFixture.Usage("original")).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        await fixture.WriteAsync("changing.jsonl", InfrastructureFixture.Meta("replacement"), InfrastructureFixture.Message("new history")).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Null(await fixture.Viewer.GetSessionAsync("original", CancellationToken).ConfigureAwait(true));
        Assert.NotNull(await fixture.Viewer.GetSessionAsync("replacement", CancellationToken).ConfigureAwait(true));
        var moved = Path.Combine(fixture.Archive, "changing.jsonl");
        File.Move(path, moved);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, (await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).SessionCount);
        File.Delete(moved);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(0, (await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).SessionCount);
    }

    [Fact]
    public async Task UnavailableSourcesRetainDataAndExposeStaleness()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("kept.jsonl", InfrastructureFixture.Meta("kept"), InfrastructureFixture.Usage("kept")).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Directory.Move(fixture.Sources, fixture.Sources + "-offline");
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var status = await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true);
        Assert.False(status.InitialIndexComplete);
        Assert.Equal(1, status.SessionCount);
        Assert.Contains(status.Sources, item => !item.Available && item.Error is not null);
        Assert.NotNull(status.LastSuccessfulScanUtc);
    }

    [Fact]
    public async Task CompetingIndexersSkipOwnedLockThenCatchUp()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        using var other = fixture.CreateServices();
        await other.GetRequiredService<IViewerService>().GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        await fixture.WriteAsync("locked.jsonl", InfrastructureFixture.Meta("locked"), InfrastructureFixture.Usage("locked")).ConfigureAwait(true);
        using (var held = new FileStream(Path.Combine(fixture.Data, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
            Assert.Equal(0, (await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).SessionCount);
        }

        await Task.WhenAll(fixture.Indexer.RefreshAsync(CancellationToken), other.GetRequiredService<SessionIndexer>().RefreshAsync(CancellationToken)).ConfigureAwait(true);
        Assert.Equal(110, (await fixture.Viewer.GetSessionAsync("locked", CancellationToken).ConfigureAwait(true))!.Detail.OwnUsage.Tokens.TotalTokens);
    }

    [Fact]
    public async Task TranscriptSearchPagingAndRawChunksPreserveUntrustedContent()
    {
        using var fixture = new InfrastructureFixture();
        var hostile = "<script>alert('telescope')</script> " + new string('x', 2500);
        await fixture.WriteAsync("transcript.jsonl", InfrastructureFixture.Meta("transcript"), InfrastructureFixture.Message(hostile), InfrastructureFixture.Record("event_msg", new { type = "item_completed", item = new { text = hostile } }, 7), InfrastructureFixture.Record("response_item", new { type = "function_call", arguments = "{\"telescope\":true}" }, 8), InfrastructureFixture.Record("response_item", new { type = "function_call_output", output = "telescope result" }, 9), InfrastructureFixture.Record("response_item", new { type = "reasoning", encrypted_content = "not-plaintext" }, 10)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var page = await fixture.Viewer.ReadEventsAsync("transcript", limit: 2, cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);
        var continuation = await fixture.Viewer.ReadEventsAsync("transcript", page.NextCursor, cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.DoesNotContain(continuation.Items, item => item.Type == "event:item_completed");
        Assert.Contains(continuation.Items, item => item.Encrypted && item.Text == "Encrypted reasoning is unavailable.");
        var message = Assert.Single(page.Items, item => item.Role == "user");
        Assert.StartsWith("<script>", message.Text, StringComparison.Ordinal);
        Assert.True(message.Truncated);
        var first = await fixture.Viewer.ReadLogRecordAsync("transcript", message.Id, limit: 100, cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.NotNull(first);
        Assert.Equal(100, first.NextOffset);
        var second = await fixture.Viewer.ReadLogRecordAsync("transcript", message.Id, first.NextOffset.GetValueOrDefault(), 32000, CancellationToken).ConfigureAwait(true);
        Assert.NotNull(second);
        Assert.Null(second.NextOffset);
        Assert.Equal(first.TotalLength, first.Text.Length + second.Text.Length);
        var search = await fixture.Viewer.SearchLogsAsync("telescope", "transcript", limit: 1, cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Single(search.Items);
        Assert.NotNull(search.NextCursor);
        Assert.NotEmpty((await fixture.Viewer.SearchLogsAsync("telescope", "transcript", search.NextCursor, cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
        Assert.Empty((await fixture.Viewer.SearchLogsAsync("\" OR telescope", cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
        Assert.Null(await fixture.Viewer.ReadLogRecordAsync("../outside", message.Id, cancellationToken: CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task QueriesFilterSessionsAndAggregateUsageWithEditablePricing()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("a.jsonl", InfrastructureFixture.Meta("a", title: "Alpha telescope"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("a"), InfrastructureFixture.Usage("a", "a-response")).ConfigureAwait(true);
        await fixture.WriteAsync("b.jsonl", InfrastructureFixture.Meta("b", title: "Beta camera"), InfrastructureFixture.Context("unknown-model"), InfrastructureFixture.Usage("b", "b-response")).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var listing = await fixture.Viewer.ListSessionsAsync(new SessionFilter(Limit: 1), CancellationToken).ConfigureAwait(true);
        Assert.Single(listing.Items);
        Assert.NotNull(listing.NextCursor);
        var next = await fixture.Viewer.ListSessionsAsync(new SessionFilter(Cursor: listing.NextCursor, Limit: 1), CancellationToken).ConfigureAwait(true);
        Assert.Single(next.Items);
        Assert.Null(next.NextCursor);
        Assert.Single((await fixture.Viewer.ListSessionsAsync(new SessionFilter(Query: "ALPHA", Project: "D:\\project", Model: "gpt-6-astra", From: InfrastructureFixture.Now.AddDays(-1), To: InfrastructureFixture.Now), CancellationToken).ConfigureAwait(true)).Items);
        Assert.Empty((await fixture.Viewer.ListSessionsAsync(new SessionFilter(Project: "absent"), CancellationToken).ConfigureAwait(true)).Items);
        var filters = await fixture.Viewer.GetFiltersAsync(CancellationToken).ConfigureAwait(true);
        Assert.Single(filters.Projects);
        Assert.Equal(2, filters.Models.Count);
        var report = await fixture.Viewer.GetUsageAsync(new UsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal(3, report.Periods.Count);
        Assert.Equal(220, report.Periods[0].Totals.Tokens.TotalTokens);
        Assert.Equal(110, report.Periods[0].Totals.UnpricedTokens);
        Assert.Equal(31, report.Buckets.Count);
        var filtered = await fixture.Viewer.GetUsageAsync(new UsageQuery("rolling", "UTC", "D:\\project", "gpt-6-astra", InfrastructureFixture.Now.AddDays(-1), InfrastructureFixture.Now), CancellationToken).ConfigureAwait(true);
        Assert.Equal(110, filtered.Periods[0].Totals.Tokens.TotalTokens);
        var settings = await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        var catalog = settings.Pricing with { ModelAliases = new Dictionary<string, string> { ["unknown-model"] = "gpt-6-astra" }, TierOverrides = new Dictionary<string, string> { ["gpt-6-astra"] = "standard", ["unknown-model"] = "standard" } };
        await fixture.Viewer.UpdateSettingsAsync(settings with { Pricing = catalog, Timezone = "UTC" }, CancellationToken).ConfigureAwait(true);
        var repriced = await fixture.Viewer.GetUsageAsync(new UsageQuery(), CancellationToken).ConfigureAwait(true);
        Assert.True(repriced.Periods[0].Totals.IsPricingComplete);
        Assert.Equal("UTC", repriced.Timezone);
        Assert.True(repriced.Periods[0].Totals.KnownCostUsd > report.Periods[0].Totals.KnownCostUsd);
    }

    [Fact]
    public async Task ChangingSourcesAndRebuildUpdateSharedIndex()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("old.jsonl", InfrastructureFixture.Meta("old")).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var settings = await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        await fixture.Viewer.UpdateSettingsAsync(settings with { Sources = [fixture.Archive] }, CancellationToken).ConfigureAwait(true);
        Assert.False((await fixture.Viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).InitialIndexComplete);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Null(await fixture.Viewer.GetSessionAsync("old", CancellationToken).ConfigureAwait(true));
        await fixture.Viewer.UpdateSettingsAsync(settings, CancellationToken).ConfigureAwait(true);
        await fixture.Viewer.RebuildAsync(CancellationToken).ConfigureAwait(true);
        Assert.NotNull(await fixture.Viewer.GetSessionAsync("old", CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task MalformedUnknownAndInvalidUsageAreReportedWithoutBreakingImport()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("odd.jsonl", InfrastructureFixture.Meta("odd"), "{broken", "[]", " ", InfrastructureFixture.Record("new-event-kind", new { text = "future event" }), InfrastructureFixture.Record("token_usage_record", new { thread_id = "odd", usage = InfrastructureFixture.Tokens(-1) }), InfrastructureFixture.Record("event_msg", new { type = "token_count", info = new { total_token_usage = InfrastructureFixture.Tokens(-1) } }), InfrastructureFixture.Record("event_msg", new { type = "token_count", info = new { } }), InfrastructureFixture.Usage("foreign"), InfrastructureFixture.Record("token_usage_record", new { thread_id = "odd", usage = InfrastructureFixture.Tokens(100) }, 9)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var result = await fixture.Viewer.GetSessionAsync("odd", CancellationToken).ConfigureAwait(true);
        Assert.NotNull(result);
        Assert.Equal(110, result.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.True(result.Index.Warnings.Count >= 4);
        Assert.Contains((await fixture.Viewer.ReadEventsAsync("odd", cancellationToken: CancellationToken).ConfigureAwait(true)).Items, item => item.Type == "new-event-kind");
    }

    [Fact]
    public async Task LargeBatchesAndOptionalTitlesImportCompletely()
    {
        using var fixture = new InfrastructureFixture();
        var lines = new List<object> { InfrastructureFixture.Meta("large") };
        lines.AddRange(Enumerable.Range(0, 210).Select(index => InfrastructureFixture.Message($"Item {index}", ordinal: index + 2)));
        await fixture.WriteAsync("large.jsonl", [.. lines]).ConfigureAwait(true);
        var indexPath = Path.Combine(Path.GetDirectoryName(fixture.Sources)!, "session_index.jsonl");
        await File.WriteAllTextAsync(indexPath, "bad-json\n{\"id\":\"large\",\"thread_name\":\"Friendly title\"}\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var result = await fixture.Viewer.GetSessionAsync("large", CancellationToken).ConfigureAwait(true);
        Assert.Equal("Friendly title", result!.Detail.Session.Title);
        Assert.Equal(211, result.Index.EventCount);
    }

    [Fact]
    public async Task EmptyFileCanBecomeACompleteSession()
    {
        using var fixture = new InfrastructureFixture();
        var path = Path.Combine(fixture.Sources, "empty.jsonl");
        await File.WriteAllTextAsync(path, string.Empty, CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        await fixture.WriteAsync("empty.jsonl", InfrastructureFixture.Meta("not-empty"), InfrastructureFixture.Message("Unicode café 🚀")).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.NotNull(await fixture.Viewer.GetSessionAsync("not-empty", CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task InvalidQueriesFailAsClientErrors()
    {
        using var fixture = new InfrastructureFixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Viewer.ListSessionsAsync(new SessionFilter(Limit: 0), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ListSessionsAsync(new SessionFilter(Cursor: "bad cursor"), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ListSessionsAsync(new SessionFilter(Cursor: Convert.ToBase64String(Encoding.UTF8.GetBytes("-1"))), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Viewer.ReadEventsAsync("id", limit: 101, cancellationToken: CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Viewer.ReadLogRecordAsync("id", "event", -1, cancellationToken: CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.SearchLogsAsync(" ", cancellationToken: CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.GetUsageAsync(new UsageQuery(From: InfrastructureFixture.Now, To: InfrastructureFixture.Now), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.GetUsageAsync(new UsageQuery(From: InfrastructureFixture.Now.AddYears(-20)), CancellationToken)).ConfigureAwait(true);
        var settings = await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.UpdateSettingsAsync(settings with { Sources = ["relative"] }, CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task NewerDatabaseSchemaIsRejected()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Data, "session-viewer.db")}"))
        {
            await connection.OpenAsync(CancellationToken).ConfigureAwait(true);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            await command.ExecuteNonQueryAsync(CancellationToken).ConfigureAwait(true);
        }

        using var other = fixture.CreateServices();
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.GetRequiredService<IViewerService>().GetSettingsAsync(CancellationToken)).ConfigureAwait(true);
    }
}
