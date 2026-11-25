using System.Diagnostics;
using Turnglass.Core;

namespace Turnglass.Tests;

public sealed class InfrastructurePagingTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MixedTranscriptsKeepDistinctEventMessagesAndIdentifyToolCalls()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("mixed.jsonl", InfrastructureFixture.Meta("mixed"),
            InfrastructureFixture.Message("Message A", ordinal: 2),
            InfrastructureFixture.Record("event_msg", new { type = "user_message", message = "Message A" }, 3),
            InfrastructureFixture.Record("event_msg", new { type = "user_message", message = "Message B" }, 4),
            InfrastructureFixture.Record("event_msg", new { type = "agent_message", message = "Message A" }, 5),
            InfrastructureFixture.Record("event_msg", new { type = "item_completed", item = new { id = "separate", role = "assistant", text = "Distinct completed item" } }, 6),
            InfrastructureFixture.Record("response_item", new { type = "reasoning", summary = new[] { new { text = "Reasoning A" } } }, 7),
            InfrastructureFixture.Record("event_msg", new { type = "agent_reasoning", text = "Reasoning B" }, 8),
            InfrastructureFixture.Record("response_item", new { type = "function_call", name = "exec_command", call_id = "call-123", arguments = "{\"cmd\":\"echo test\"}" }, 9),
            InfrastructureFixture.Record("response_item", new { type = "function_call_output", call_id = "call-123", output = "test" }, 10),
            InfrastructureFixture.Record("response_item", new { type = "custom_tool_call", name = "apply_patch", call_id = "call-456", input = "patch" }, 11)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var events = await fixture.Viewer.ReadEventsAsync("mixed", cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Single(events.Items, item => item.Text == "Message A" && item.Role == "user");
        Assert.Contains(events.Items, item => item.Text == "Message B" && item.Role == "user");
        Assert.Contains(events.Items, item => item.Text == "Message A" && item.Role == "assistant");
        Assert.Contains(events.Items, item => item.Text == "Distinct completed item");
        Assert.Contains(events.Items, item => item.Text == "Reasoning B");
        var call = Assert.Single(events.Items, item => item.Type == "function_call");
        Assert.Contains("Tool: exec_command", call.Text, StringComparison.Ordinal);
        Assert.Contains("Call: call-123", call.Text, StringComparison.Ordinal);
        Assert.Contains(events.Items, item => item.Type == "function_call_output" && item.Text.Contains("Call: call-123", StringComparison.Ordinal));
        Assert.Single((await fixture.Viewer.SearchLogsAsync("apply_patch", cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
    }

    [Fact]
    public async Task SessionPagesAreStableWhenNewerSessionsArrive()
    {
        using var fixture = new InfrastructureFixture();
        for (var index = 0; index < 3; index++)
        {
            await fixture.WriteAsync($"{index}.jsonl", InfrastructureFixture.Record("session_meta", new { id = $"session-{index}" }, timestamp: InfrastructureFixture.Now.AddMinutes(-index - 1))).ConfigureAwait(true);
        }

        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var first = await fixture.Viewer.ListSessionsAsync(new SessionFilter(Limit: 2), CancellationToken).ConfigureAwait(true);
        Assert.Equal(["session-0", "session-1"], first.Items.Select(item => item.Session.Id));
        await fixture.WriteAsync("new.jsonl", InfrastructureFixture.Record("session_meta", new { id = "newest" }, timestamp: InfrastructureFixture.Now)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var second = await fixture.Viewer.ListSessionsAsync(new SessionFilter(Cursor: first.NextCursor, Limit: 2), CancellationToken).ConfigureAwait(true);
        Assert.Equal("session-2", Assert.Single(second.Items).Session.Id);
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ListSessionsAsync(new SessionFilter(Query: "different", Cursor: first.NextCursor), CancellationToken)).ConfigureAwait(true);
        await fixture.Viewer.RebuildAsync(CancellationToken).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ListSessionsAsync(new SessionFilter(Cursor: first.NextCursor), CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task SearchAndEventPagesUseStablePositionsAndRejectMismatchedCursors()
    {
        using var fixture = new InfrastructureFixture();
        var records = new List<object> { InfrastructureFixture.Meta("search") };
        for (var index = 0; index < 3; index++)
        {
            records.Add(InfrastructureFixture.Record("response_item", new { type = "message", role = "user", content = new[] { new { text = $"needle {index}" } } }, index + 2, InfrastructureFixture.Now.AddMinutes(-3 + index)));
        }

        var path = await fixture.WriteAsync("search.jsonl", [.. records]).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var first = await fixture.Viewer.SearchLogsAsync("needle", "search", limit: 2, cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Equal(["needle 2", "needle 1"], first.Items.Select(item => item.Text));
        var eventPage = await fixture.Viewer.ReadEventsAsync("search", limit: 2, cancellationToken: CancellationToken).ConfigureAwait(true);
        await File.AppendAllTextAsync(path, InfrastructureFixture.Serialize(InfrastructureFixture.Record("response_item", new { type = "message", role = "user", content = new[] { new { text = "needle newest" } } }, 5, InfrastructureFixture.Now)) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var second = await fixture.Viewer.SearchLogsAsync("needle", "search", first.NextCursor, 2, CancellationToken).ConfigureAwait(true);
        Assert.Equal("needle 0", Assert.Single(second.Items).Text);
        var nextEvents = await fixture.Viewer.ReadEventsAsync("search", eventPage.NextCursor, cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.Equal(["needle 1", "needle 2", "needle newest"], nextEvents.Items.Select(item => item.Text));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.SearchLogsAsync("different", "search", first.NextCursor, cancellationToken: CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ReadEventsAsync("other-session", eventPage.NextCursor, cancellationToken: CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Viewer.ReadEventsAsync("search", first.NextCursor, cancellationToken: CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task LargeTranscriptPagesStayWithinTheMcpTimeoutBudget()
    {
        using var fixture = new InfrastructureFixture();
        var records = new List<object> { InfrastructureFixture.Meta("large") };
        for (var index = 0; index < 3000; index++)
        {
            var ordinal = (index * 3) + 2;
            var id = $"item-{index}";
            var text = $"Response {index}";
            records.Add(InfrastructureFixture.Record("response_item", new { type = "message", id, role = "assistant", content = new[] { new { text } } }, ordinal));
            records.Add(InfrastructureFixture.Record("event_msg", new { type = "item_completed", item = new { id, role = "assistant", text } }, ordinal + 1));
            records.Add(InfrastructureFixture.Record("event_msg", new { type = "agent_message", message = $"Separate {index}" }, ordinal + 2));
        }

        await fixture.WriteAsync("large.jsonl", [.. records]).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var timer = Stopwatch.StartNew();
        var first = await fixture.Viewer.ReadEventsAsync("large", cancellationToken: CancellationToken).ConfigureAwait(true);
        var second = await fixture.Viewer.ReadEventsAsync("large", first.NextCursor, cancellationToken: CancellationToken).ConfigureAwait(true);
        timer.Stop();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), "Two transcript pages exceeded the four-second budget below the MCP tool timeout.");
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(50, second.Items.Count);
        Assert.NotNull(second.NextCursor);
        Assert.DoesNotContain(first.Items.Concat(second.Items), item => item.Type == "event:item_completed");
        Assert.Contains(first.Items, item => item.Type == "event:agent_message");
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id), StringComparer.Ordinal));
    }
}
