using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class InfrastructureWorkerTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BackgroundWorkerIndexesAppendsAndStopsCleanly()
    {
        using var fixture = new InfrastructureFixture();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTurnglass(new ViewerOptions { DataDirectory = fixture.Data, Sources = [fixture.Sources, fixture.Archive] });
        using var host = builder.Build();
        await host.StartAsync(CancellationToken).ConfigureAwait(true);
        var viewer = host.Services.GetRequiredService<IViewerService>();
        await EventuallyAsync(async () => (await viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).InitialIndexComplete).ConfigureAwait(true);
        var path = await fixture.WriteAsync("watched.jsonl", InfrastructureFixture.Meta("watched"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("watched"), InfrastructureFixture.Usage("watched")).ConfigureAwait(true);
        await EventuallyAsync(async () => await viewer.GetSessionAsync("watched", CancellationToken).ConfigureAwait(true) is not null).ConfigureAwait(true);
        await File.AppendAllTextAsync(path, InfrastructureFixture.Serialize(InfrastructureFixture.Usage("watched", "next", ordinal: 5)) + "\n", CancellationToken).ConfigureAwait(true);
        await EventuallyAsync(async () => (await viewer.GetSessionAsync("watched", CancellationToken).ConfigureAwait(true))!.Detail.OwnUsage.Tokens.TotalTokens == 220).ConfigureAwait(true);
        var settings = await viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true);
        await viewer.UpdateSettingsAsync(settings with { Sources = [fixture.Archive] }, CancellationToken).ConfigureAwait(true);
        File.Move(path, Path.Combine(fixture.Archive, "watched.jsonl"));
        await EventuallyAsync(async () => (await viewer.GetIndexStatusAsync(CancellationToken).ConfigureAwait(true)).InitialIndexComplete).ConfigureAwait(true);
        await host.StopAsync(CancellationToken).ConfigureAwait(true);
        using var lockStream = new FileStream(Path.Combine(fixture.Data, "index.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(lockStream.CanWrite);
    }

    [Fact]
    public async Task SamePrefixReplacementIsDetectedByCommittedTail()
    {
        using var fixture = new InfrastructureFixture();
        var prefix = InfrastructureFixture.Record("session_meta", new { id = "same", cwd = "D:\\project", title = new string('p', 400) });
        await fixture.WriteAsync("same.jsonl", prefix, InfrastructureFixture.Usage("same", "before", 100)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        await fixture.WriteAsync("same.jsonl", prefix, InfrastructureFixture.Usage("same", "after-", 200)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var session = await fixture.Viewer.GetSessionAsync("same", CancellationToken).ConfigureAwait(true);
        Assert.Equal(210, session!.Detail.OwnUsage.Tokens.TotalTokens);
    }

    [Fact]
    public async Task MissingRequiredCountsAndTimestampsCannotAppearAsValidZeroUsage()
    {
        using var fixture = new InfrastructureFixture();
        var overflowingCacheCounts = new { input_tokens = long.MaxValue, output_tokens = 0, cached_input_tokens = long.MaxValue - 1, cache_write_input_tokens = long.MaxValue - 1 };
        await fixture.WriteAsync("invalid.jsonl", InfrastructureFixture.Meta("invalid"), InfrastructureFixture.Record("token_usage_record", new { thread_id = "invalid" }), InfrastructureFixture.Record("token_usage_record", new { thread_id = "invalid", usage = new { input_tokens = "100", output_tokens = 2 } }), new { type = "token_usage_record", payload = new { thread_id = "invalid", usage = InfrastructureFixture.Tokens(100) } }, InfrastructureFixture.Record("token_usage_record", new { thread_id = "invalid", usage = new { input_tokens = long.MaxValue, output_tokens = 2 } }), InfrastructureFixture.Record("token_usage_record", new { thread_id = "invalid", usage = overflowingCacheCounts }), InfrastructureFixture.Legacy(overflowingCacheCounts)).ConfigureAwait(true);
        var indexPath = Path.Combine(Path.GetDirectoryName(fixture.Sources)!, "session_index.jsonl");
        await File.WriteAllTextAsync(indexPath, "[]\n{\"id\":4,\"thread_name\":false}\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var session = await fixture.Viewer.GetSessionAsync("invalid", CancellationToken).ConfigureAwait(true);
        Assert.Equal(0, session!.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.Contains(session.Index.Warnings, warning => warning.Contains("timestamp", StringComparison.Ordinal));
        Assert.Contains(session.Index.Warnings, warning => warning.Contains("Invalid token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousCutoverProducesExplicitDiagnosticAndPreservesLegacyPrefix()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("mixed.jsonl", InfrastructureFixture.Meta("mixed"), InfrastructureFixture.Context(), InfrastructureFixture.Settings("mixed"), InfrastructureFixture.Legacy(InfrastructureFixture.Tokens(100)), InfrastructureFixture.Usage("mixed", input: 50, cumulative: InfrastructureFixture.Tokens(1000), ordinal: 8)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var session = await fixture.Viewer.GetSessionAsync("mixed", CancellationToken).ConfigureAwait(true);
        Assert.Equal(170, session!.Detail.OwnUsage.Tokens.TotalTokens);
        Assert.Contains(session.Index.Warnings, warning => warning.Contains("transition", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BinaryUtf8BoundariesAndBomPreserveTextAndCheckpoints()
    {
        using var fixture = new InfrastructureFixture();
        var message = new string('x', 65520) + "café🚀";
        var data = "\uFEFF" + InfrastructureFixture.Serialize(InfrastructureFixture.Meta("unicode")) + "\r\n" + InfrastructureFixture.Serialize(InfrastructureFixture.Message(message)) + "\r\n";
        await File.WriteAllTextAsync(Path.Combine(fixture.Sources, "unicode.jsonl"), data, CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var events = await fixture.Viewer.ReadEventsAsync("unicode", cancellationToken: CancellationToken).ConfigureAwait(true);
        var entry = Assert.Single(events.Items, item => item.Role == "user");
        var accumulated = string.Empty;
        int? offset = 0;
        while (offset.HasValue)
        {
            var chunk = await fixture.Viewer.ReadLogRecordAsync("unicode", entry.Id, offset.GetValueOrDefault(), 32000, CancellationToken).ConfigureAwait(true);
            accumulated += chunk!.Text;
            offset = chunk.NextOffset;
        }

        using var document = JsonDocument.Parse(accumulated);
        Assert.Equal(message, document.RootElement.GetProperty("payload").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task QuerySnapshotsStayCoherentDuringConcurrentRebuilds()
    {
        using var fixture = new InfrastructureFixture();
        for (var index = 0; index < 12; index++)
        {
            var id = $"session-{index}";
            await fixture.WriteAsync($"{id}.jsonl", InfrastructureFixture.Meta(id), InfrastructureFixture.Usage(id, $"response-{index}")).ConfigureAwait(true);
        }

        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var rebuilding = Task.Run(async () =>
        {
            for (var iteration = 0; iteration < 5; iteration++)
            {
                await fixture.Viewer.RebuildAsync(CancellationToken).ConfigureAwait(true);
            }
        }, CancellationToken);
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var report = await fixture.Viewer.GetUsageAsync(new UsageQuery(), CancellationToken).ConfigureAwait(true);
            Assert.Equal(report.Index.SessionCount * 110L, report.Periods[0].Totals.Tokens.TotalTokens);
            Assert.Equal(report.Index.SessionCount * 2, report.Index.EventCount);
        }

        await rebuilding.ConfigureAwait(true);
    }

    [Fact]
    public async Task SchemaLockHasBoundedWaitAndCanRecover()
    {
        using var fixture = new InfrastructureFixture();
        Directory.CreateDirectory(fixture.Data);
        using (var held = new FileStream(Path.Combine(fixture.Data, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Viewer.GetSettingsAsync(CancellationToken)).ConfigureAwait(true);
        }

        Assert.NotNull(await fixture.Viewer.GetSettingsAsync(CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task BackgroundWorkerSurvivesSchemaLockTimeoutAndRetries()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("retry.jsonl", InfrastructureFixture.Meta("retry"), InfrastructureFixture.Usage("retry")).ConfigureAwait(true);
        Directory.CreateDirectory(fixture.Data);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTurnglass(new ViewerOptions { DataDirectory = fixture.Data, Sources = [fixture.Sources, fixture.Archive] });
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using (var held = new FileStream(Path.Combine(fixture.Data, "index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await host.StartAsync(CancellationToken).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromMilliseconds(5400), CancellationToken).ConfigureAwait(true);
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        }

        var viewer = host.Services.GetRequiredService<IViewerService>();
        await EventuallyAsync(async () => await viewer.GetSessionAsync("retry", CancellationToken).ConfigureAwait(true) is not null).ConfigureAwait(true);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Equal(110, (await viewer.GetSessionAsync("retry", CancellationToken).ConfigureAwait(true))!.Detail.OwnUsage.Tokens.TotalTokens);
        await host.StopAsync(CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task RawChunksNeverSplitUnicodeSurrogatePairs()
    {
        using var fixture = new InfrastructureFixture();
        const string raw = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"text\":\"A🚀B\"}]},\"ordinal\":2,\"timestamp\":\"2026-11-21T19:00:00Z\"}";
        await fixture.WriteAsync("unicode-chunk.jsonl", InfrastructureFixture.Meta("unicode-chunk"), raw).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var events = await fixture.Viewer.ReadEventsAsync("unicode-chunk", cancellationToken: CancellationToken).ConfigureAwait(true);
        var entry = Assert.Single(events.Items, item => item.Role == "user");
        var rocketOffset = raw.IndexOf("🚀", StringComparison.Ordinal);
        var first = await fixture.Viewer.ReadLogRecordAsync("unicode-chunk", entry.Id, 0, rocketOffset + 1, CancellationToken).ConfigureAwait(true);
        Assert.NotNull(first);
        Assert.Equal(rocketOffset, first.NextOffset);
        var second = await fixture.Viewer.ReadLogRecordAsync("unicode-chunk", entry.Id, first.NextOffset.GetValueOrDefault(), cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.NotNull(second);
        Assert.Equal(raw, first.Text + second.Text);
        var serialized = JsonSerializer.Serialize(first.Text + second.Text);
        Assert.Equal(raw, JsonSerializer.Deserialize<string>(serialized));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ReadLogRecordAsync("unicode-chunk", entry.Id, rocketOffset + 1, cancellationToken: CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ReadLogRecordAsync("unicode-chunk", entry.Id, rocketOffset, 1, CancellationToken)).ConfigureAwait(true);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!await predicate().ConfigureAwait(true))
        {
            await Task.Delay(30, timeout.Token).ConfigureAwait(true);
        }
    }
}
