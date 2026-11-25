using System.Net;
using System.Net.Http.Json;
using ModelContextProtocol;
using Turnglass.Core;
using Turnglass.Mcp;

namespace Turnglass.Tests;

public sealed class CopilotTransportTests
{
    [Fact]
    public async Task RestAndMcpUseSameCopilotReportsAndPreserveAssumptions()
    {
        var copilot = new HostFakeCopilot();
        using var factory = new HostApplicationFactory(new HostFakeViewer(), copilot: copilot);
        using var client = factory.CreateClient();
        var rest = await client.GetFromJsonAsync<CopilotUsageReport>("/api/copilot/usage?mode=rolling&timezone=UTC&client=shared&model=gpt-5.4&from=2026-11-01T00:00:00Z&to=2026-11-21T00:00:00Z", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotNull(rest);
        var query = copilot.LastQuery!;
        Assert.Equal("shared", query.Client);
        Assert.Equal("gpt-5.4", query.Model);
        var tools = new CopilotTools(copilot);
        var mcp = await tools.GetCopilotUsageAsync(query.Mode, query.Timezone, query.Client, query.Model, query.From, query.To, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(rest.Periods[0].Totals.Tokens, mcp.Periods[0].Totals.Tokens);
        Assert.Equal(rest.PricingAssumptions, mcp.PricingAssumptions);
        Assert.Equal(rest.UnknownCacheReadRecords, mcp.UnknownCacheReadRecords);
        var status = await tools.GetCopilotIndexStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(status.InitialScanComplete);
        var restStatus = await client.GetFromJsonAsync<CopilotIndexStatus>("/api/copilot/index", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(status.RecordCount, restStatus!.RecordCount);
        var filters = await client.GetFromJsonAsync<CopilotFilterOptions>("/api/copilot/filters", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Contains("shared", filters!.Clients);
    }

    [Fact]
    public async Task CopilotSettingsAndRescanAreIndependentAndOriginProtected()
    {
        var codex = new HostFakeViewer();
        var original = codex.Settings;
        var copilot = new HostFakeCopilot();
        using var factory = new HostApplicationFactory(codex, copilot: copilot);
        using var client = factory.CreateClient();
        var settings = await client.GetFromJsonAsync<CopilotSettings>("/api/copilot/settings", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(settings!.AutoDiscover);
        using var saved = await client.PutAsJsonAsync("/api/copilot/settings", new CopilotSettings(false, [new("shared", "C:\\fixture")]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        saved.EnsureSuccessStatusCode();
        Assert.Single(copilot.Settings.Sources);
        using var rescan = await client.PostAsync("/api/copilot/index/rescan", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        rescan.EnsureSuccessStatusCode();
        Assert.True(copilot.Rescanned);
        Assert.Same(original, codex.Settings);
        Assert.False(codex.Rebuilt);
        using var untrusted = new HttpRequestMessage(HttpMethod.Post, "/api/copilot/index/rescan");
        untrusted.Headers.Add("Origin", "https://evil.example");
        using var rejected = await client.SendAsync(untrusted, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var invalid = await client.GetAsync("/api/copilot/usage?from=invalid", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task McpErrorsDoNotLeakLocalPathsAndCancellationRemainsActionable()
    {
        var fake = new HostFakeCopilot { Failure = new IOException("C:\\private\\usage.jsonl") };
        var tools = new CopilotTools(fake);
        var error = await Assert.ThrowsAsync<McpException>(() => tools.GetCopilotIndexStatusAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        fake.Failure = new ArgumentException("Unknown client");
        error = await Assert.ThrowsAsync<McpException>(() => tools.GetCopilotUsageAsync(cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal("Unknown client", error.Message);
        fake.Failure = new OperationCanceledException();
        error = await Assert.ThrowsAsync<McpException>(() => tools.GetCopilotIndexStatusAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("busy", error.Message, StringComparison.Ordinal);
    }
}
