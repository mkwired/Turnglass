using ModelContextProtocol;
using Turnglass.Mcp;

namespace Turnglass.Tests;

public sealed class McpTests
{
    [Fact]
    public async Task ToolsShareTheApplicationQueryService()
    {
        var viewer = new HostFakeViewer();
        var tools = new SessionTools(viewer);
        var sessions = await tools.ListSessionsAsync(query: "test", project: "sample", limit: 5, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Single(sessions.Items);
        Assert.Equal("test", viewer.LastFilter?.Query);
        Assert.Equal(5, viewer.LastFilter?.Limit);
        var detail = await tools.GetSessionAsync("session-a", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("session-a", detail.Detail.Session.Id);
        var events = await tools.ReadSessionEventsAsync("session-a", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Single(events.Items);
        var chunk = await tools.ReadLogRecordAsync("session-a", "event-a", 3, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("event-a", chunk.EventId);
        Assert.Equal(3, viewer.LastOffset);
        var search = await tools.SearchSessionLogsAsync("phrase", "session-a", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Single(search.Items);
        Assert.Equal("phrase", viewer.LastSearch);
        var usage = await tools.GetUsageAsync("rolling", "UTC", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("rolling", usage.Mode);
        Assert.Equal("UTC", viewer.LastUsageQuery?.Timezone);
        var status = await tools.GetIndexStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(status.InitialIndexComplete);
    }

    [Fact]
    public async Task MissingSessionsAndRecordsReturnProtocolErrors()
    {
        var tools = new SessionTools(new HostFakeViewer { Missing = true });
        await Assert.ThrowsAsync<McpException>(() => tools.GetSessionAsync("unknown", TestContext.Current.CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<McpException>(() => tools.ReadLogRecordAsync("unknown", "unknown", cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task IndexFailuresAreActionableAndDoNotExposeLocalPaths()
    {
        var viewer = new HostFakeViewer { Failure = new IOException("C:\\private\\secret.jsonl") };
        var tools = new SessionTools(viewer);
        var error = await Assert.ThrowsAsync<McpException>(() => tools.GetIndexStatusAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("retry", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
        viewer.Failure = new ArgumentException("Invalid cursor");
        error = await Assert.ThrowsAsync<McpException>(() => tools.GetIndexStatusAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("Invalid cursor", error.Message, StringComparison.Ordinal);
        viewer.Failure = new OperationCanceledException();
        error = await Assert.ThrowsAsync<McpException>(() => tools.GetIndexStatusAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("busy", error.Message, StringComparison.Ordinal);
    }
}
