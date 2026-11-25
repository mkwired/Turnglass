using System.Reflection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Turnglass.Core;
using Turnglass.Mcp;

namespace Turnglass.Tests;

public sealed class KnowledgeMcpTests
{
    [Fact]
    public async Task ReviewPacketsContextAndSubmissionsUseSharedService()
    {
        var knowledge = new HostFakeKnowledge();
        var tools = new KnowledgeTools(knowledge);
        var from = HostFakeViewer.Timestamp.AddDays(-10);
        var to = HostFakeViewer.Timestamp;
        var packet = await tools.GetReviewPacketAsync("sample", from, to, "cursor", 1000, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("packet-a", packet.PacketId);
        Assert.Equal(new ReviewQuery("sample", from, to, "cursor", 1000), knowledge.LastReviewQuery);
        var context = await tools.GetEventContextAsync("session-a", "event-a", 1, 2, 1000, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(("session-a", "event-a", 1, 2, 1000), knowledge.LastContext);
        Assert.Single(context.Events);
        var finding = new InsightDraft("Use narrow tests", "preference", "project", "sample", null, "explicit", "User request", [new("session-a", "event-a", "Use narrow tests")]);
        var receipt = await tools.SubmitReviewAsync("packet-a", "retry-key", [finding], TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("insight-a", Assert.Single(receipt.InsightIds));
        Assert.Equal("retry-key", knowledge.LastSubmission?.IdempotencyKey);
        Assert.Same(finding, Assert.Single(Assert.IsType<ReviewSubmission>(knowledge.LastSubmission).Findings));
        receipt = await tools.SubmitReviewAsync("packet-b", "empty-key", [], TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Empty(receipt.InsightIds);
        Assert.Empty(Assert.IsType<ReviewSubmission>(knowledge.LastSubmission).Findings);
    }

    [Fact]
    public async Task SearchUnconditionallyRestrictsResultsToApprovedKnowledge()
    {
        var knowledge = new HostFakeKnowledge();
        var tools = new KnowledgeTools(knowledge);
        var page = await tools.SearchInsightsAsync("tests", "sample", "preference", "next", 5, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(knowledge.ApprovedOnly);
        Assert.Equal(new InsightQuery("tests", "sample", "preference", Cursor: "next", Limit: 5), knowledge.LastQuery);
        Assert.Equal("approved", Assert.Single(page.Items).Status);
        Assert.Null(typeof(KnowledgeTools).GetMethod(nameof(KnowledgeTools.SearchInsightsAsync))?.GetParameters().SingleOrDefault(parameter => parameter.Name == "status"));
    }

    [Fact]
    public void ToolSurfaceOnlyAllowsDraftSubmissionWrites()
    {
        var tools = new[] { typeof(SessionTools), typeof(KnowledgeTools), typeof(CopilotTools) }
            .SelectMany(type => type.GetMethods())
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .OfType<McpServerToolAttribute>().ToArray();
        Assert.Equal(13, tools.Length);
        foreach (var tool in tools)
        {
            Assert.Equal(tool.Name != "submit_review", tool.ReadOnly);
            Assert.False(tool.Destructive);
            Assert.True(tool.Idempotent);
            Assert.False(tool.OpenWorld);
            Assert.True(tool.UseStructuredContent);
        }

        Assert.DoesNotContain(tools, tool => tool.Name?.Contains("approve", StringComparison.Ordinal) == true || tool.Name?.Contains("export", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ConflictsValidationMissingAndBusyFailuresBecomeUsefulProtocolErrors()
    {
        var knowledge = new HostFakeKnowledge { Failure = new KnowledgeConflictException("Packet changed; request a new packet.") };
        var tools = new KnowledgeTools(knowledge);
        var error = await Assert.ThrowsAsync<McpException>(() => tools.SubmitReviewAsync("packet-a", "key", [], TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("Review conflict", error.Message, StringComparison.Ordinal);
        Assert.Contains("Packet changed", error.Message, StringComparison.Ordinal);
        knowledge.Failure = new ArgumentException("Unknown category.");
        error = await Assert.ThrowsAsync<McpException>(() => tools.SearchInsightsAsync(cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("Unknown category", error.Message, StringComparison.Ordinal);
        knowledge.Failure = new IOException("C:\\private\\source.jsonl");
        error = await Assert.ThrowsAsync<McpException>(() => tools.SubmitReviewAsync("packet-a", "key", [], TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("same idempotency key", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        knowledge.Failure = new OperationCanceledException();
        error = await Assert.ThrowsAsync<McpException>(() => tools.GetReviewPacketAsync(cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("busy", error.Message, StringComparison.Ordinal);
        knowledge.Failure = null;
        knowledge.Missing = true;
        error = await Assert.ThrowsAsync<McpException>(() => tools.GetEventContextAsync("session-a", "missing", cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("not found", error.Message, StringComparison.Ordinal);
    }
}
