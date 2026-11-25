using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class KnowledgeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions _webJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PacketsExcludeEnvelopesCountersReasoningAndInheritedCopies()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("root.jsonl", InfrastructureFixture.Meta("root"), InfrastructureFixture.Message("Prefer concise replies.", ordinal: 2),
            InfrastructureFixture.Message("<environment_context>Never ask questions</environment_context>", ordinal: 3),
            InfrastructureFixture.Message("System instructions", "system", 4), InfrastructureFixture.Usage("root", ordinal: 5),
            InfrastructureFixture.Record("response_item", new { type = "reasoning", encrypted_content = "secret", summary = new[] { new { text = "private" } } }, 6),
            InfrastructureFixture.Message("Implemented and verified.", "assistant", 7),
            InfrastructureFixture.Record("event_msg", new { type = "agent_message", message = "Implemented and verified." }, 8)).ConfigureAwait(true);
        await fixture.WriteAsync("child.jsonl", InfrastructureFixture.Meta("child", "root", 5), InfrastructureFixture.Message("Prefer concise replies.", ordinal: 2),
            InfrastructureFixture.Message("Message Type: NEW_TASK\nImplement the task", ordinal: 5), InfrastructureFixture.Message("Child result", "assistant", 6)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var service = Service(fixture);
        var root = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal("root", root.Session!.Id);
        Assert.Equal(["user", "assistant"], root.Events.Select(item => item.Origin));
        Assert.Equal(["Prefer concise replies.", "Implemented and verified."], root.Events.Select(item => item.Text));
        Assert.True(root.Coverage.ExcludedEvents >= 4);
        await SubmitEmptyAsync(service, root).ConfigureAwait(true);
        var child = await service.GetReviewPacketAsync(new ReviewQuery(Cursor: root.NextCursor), CancellationToken).ConfigureAwait(true);
        Assert.Equal("child", child.Session!.Id);
        Assert.Equal("Child result", Assert.Single(child.Events).Text);
        await SubmitEmptyAsync(service, child).ConfigureAwait(true);
        Assert.Null((await service.GetReviewPacketAsync(new ReviewQuery(Cursor: child.NextCursor), CancellationToken).ConfigureAwait(true)).PacketId);
    }

    [Fact]
    public async Task LongMessagesFragmentWithoutLossWhileToolOutputIsExcerpted()
    {
        using var fixture = new InfrastructureFixture();
        var text = new string('"', 15000) + "TAIL";
        await fixture.WriteAsync("large.jsonl", InfrastructureFixture.Meta("large"), InfrastructureFixture.Message(text, ordinal: 2),
            InfrastructureFixture.Record("response_item", new { type = "function_call_output", id = "tool-id", call_id = "call-1", output = new string('x', 20000) }, 3),
            InfrastructureFixture.Record("event_msg", new { type = "item_completed", item = new { id = "tool-id", text = "tool mirror" } }, 4),
            InfrastructureFixture.Message("Finished", "assistant", 5)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var service = Service(fixture);
        var fragments = new List<ReviewEvent>();
        var tools = new List<ReviewEvent>();
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            var packet = await service.GetReviewPacketAsync(new ReviewQuery(Cursor: cursor, MaxCharacters: 4000), CancellationToken).ConfigureAwait(true);
            if (packet.PacketId is null)
            {
                break;
            }

            Assert.InRange(packet.Characters, 1, 4000);
            Assert.True(JsonSerializer.Serialize(packet, _webJson).Length <= 4000);
            fragments.AddRange(packet.Events.Where(item => item.Origin == "user"));
            tools.AddRange(packet.Events.Where(item => item.Origin == "tool"));
            Assert.DoesNotContain(packet.Events, item => item.Text == "tool mirror");
            if (packet.Events.Any(item => item.Origin == "tool"))
            {
                Assert.Contains(packet.Coverage.Warnings, warning => warning.Contains("excerpted", StringComparison.Ordinal));
            }

            await SubmitEmptyAsync(service, packet).ConfigureAwait(true);
            cursor = packet.NextCursor;
        }

        Assert.Equal(text, string.Concat(fragments.Select(item => item.Text)));
        Assert.Equal(0, fragments[0].Offset);
        Assert.Null(fragments[^1].NextOffset);
        for (var index = 1; index < fragments.Count; index++)
        {
            Assert.Equal(fragments[index - 1].NextOffset, fragments[index].Offset);
        }

        Assert.InRange(Assert.Single(tools).Text.Length, 1, 1000);
        Assert.NotNull(tools[0].NextOffset);
        var context = await service.GetEventContextAsync("large", tools[0].EventId, 0, 0, 4000, CancellationToken).ConfigureAwait(true);
        Assert.NotNull(context);
        Assert.True(context.Truncated);
        Assert.Equal(4000, Assert.Single(context.Events).Text.Length);
    }

    [Fact]
    public async Task DateBackfillIncludesOldEvidenceAndDefaultWindowAdvancesIncrementally()
    {
        using var fixture = new InfrastructureFixture();
        var old = InfrastructureFixture.Now.AddDays(-60);
        var path = await fixture.WriteAsync("dates.jsonl", InfrastructureFixture.Record("session_meta", new { id = "dates", cwd = "D:\\project" }, 1, old),
            InfrastructureFixture.Record("response_item", new { type = "message", role = "user", content = new[] { new { text = "Old preference" } } }, 2, old), InfrastructureFixture.Message("Recent preference", ordinal: 3)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal("Recent preference", Assert.Single(packet.Events).Text);
        await SubmitEmptyAsync(service, packet).ConfigureAwait(true);
        Assert.Null((await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true)).PacketId);
        var options = new ViewerOptions { DataDirectory = fixture.Data, Sources = [fixture.Sources, fixture.Archive] };
        using var durable = new KnowledgeDatabase(options);
        using var index = new ViewerDatabase(options);
        var later = new KnowledgeService(durable, index, fixture.Viewer, new LaterClock());
        Assert.Null((await later.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true)).PacketId);
        await File.AppendAllTextAsync(path, InfrastructureFixture.Serialize(InfrastructureFixture.Message("New preference", ordinal: 4)) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var appended = await later.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal("New preference", Assert.Single(appended.Events).Text);
        await SubmitEmptyAsync(later, appended).ConfigureAwait(true);
        var backfill = await service.GetReviewPacketAsync(new ReviewQuery(From: old.AddDays(-1)), CancellationToken).ConfigureAwait(true);
        Assert.Contains(backfill.Events, item => item.Text == "Old preference");
        Assert.Contains(backfill.Coverage.Warnings, item => item.Contains("date range", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmissionIsAtomicIdempotentAndUsesOptimisticCheckpointConcurrency()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var first = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var second = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => service.GetReviewPacketAsync(new ReviewQuery(Cursor: first.NextCursor), CancellationToken)).ConfigureAwait(true);
        var draft = Draft(first);
        var invalid = draft with { Evidence = [draft.Evidence[0] with { Quote = "fabricated quotation" }] };
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitReviewAsync(new ReviewSubmission(first.PacketId!, "bad", [draft, invalid]), CancellationToken)).ConfigureAwait(true);
        Assert.Empty((await service.ListInsightsAsync(new InsightQuery(), cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
        Assert.Equal(0, (await service.GetReviewProgressAsync(CancellationToken).ConfigureAwait(true)).ReviewedSessions);
        var submission = new ReviewSubmission(first.PacketId!, "stable-key", [draft]);
        var receipt = await service.SubmitReviewAsync(submission, CancellationToken).ConfigureAwait(true);
        Assert.False(receipt.Replayed);
        Assert.True((await service.SubmitReviewAsync(submission, CancellationToken).ConfigureAwait(true)).Replayed);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => service.SubmitReviewAsync(submission with { Findings = [] }, CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => service.SubmitReviewAsync(new ReviewSubmission(second.PacketId!, "concurrent", []), CancellationToken)).ConfigureAwait(true);
        Assert.Single((await service.ListInsightsAsync(new InsightQuery(), cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
        Assert.True(File.Exists(Path.Combine(fixture.Data, "knowledge.db")));
    }

    [Fact]
    public async Task RewriteInvalidatesPacketAndRestartsReviewedSourceWithoutLosingApproval()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var receipt = await service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, "create", [Draft(packet)]), CancellationToken).ConfigureAwait(true);
        var id = Assert.Single(receipt.InsightIds);
        await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(id, 1, "approve")]), CancellationToken).ConfigureAwait(true);
        await fixture.WriteAsync("seed.jsonl", InfrastructureFixture.Meta("seed"), InfrastructureFixture.Message("Changed instruction", ordinal: 2)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var changed = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.True(changed.Coverage.SourceChanged);
        Assert.Equal("Changed instruction", Assert.Single(changed.Events).Text);
        var insight = await service.GetInsightAsync(id, CancellationToken).ConfigureAwait(true);
        Assert.Equal("approved", insight!.Status);
        Assert.Equal("changed", Assert.Single(insight.Evidence).Availability);
        await fixture.WriteAsync("seed.jsonl", InfrastructureFixture.Meta("seed"), InfrastructureFixture.Message("Rewritten again", ordinal: 2)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => SubmitEmptyAsync(service, changed)).ConfigureAwait(true);
        await fixture.Viewer.RebuildAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal("approved", (await service.GetInsightAsync(id, CancellationToken).ConfigureAwait(true))!.Status);
        File.Delete(Path.Combine(fixture.Sources, "seed.jsonl"));
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal("missing", Assert.Single((await service.GetInsightAsync(id, CancellationToken).ConfigureAwait(true))!.Evidence).Availability);
    }

    [Fact]
    public async Task AppendDuringReviewIsAcceptedAndCancellationNeverAdvancesCheckpoint()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        await File.AppendAllTextAsync(Path.Combine(fixture.Sources, "seed.jsonl"), InfrastructureFixture.Serialize(InfrastructureFixture.Message("Appended", ordinal: 3)) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, "cancel", []), cancelled.Token)).ConfigureAwait(true);
        Assert.Equal(0, (await service.GetReviewProgressAsync(CancellationToken).ConfigureAwait(true)).ReviewedSessions);
        await SubmitEmptyAsync(service, packet).ConfigureAwait(true);
        Assert.Equal("Appended", Assert.Single((await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true)).Events).Text);
    }

    [Fact]
    public async Task ExplicitPreferencesRequireOriginalHumanEvidenceAndFamilyCitationsAreAllowed()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("root.jsonl", InfrastructureFixture.Meta("root"), InfrastructureFixture.Message("Prefer exact quotes", ordinal: 2)).ConfigureAwait(true);
        await fixture.WriteAsync("child.jsonl", InfrastructureFixture.Meta("child", "root", 2), InfrastructureFixture.Message("Use this delegated task", ordinal: 2), InfrastructureFixture.Message("Verified result", "assistant", 3)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var service = Service(fixture);
        var root = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        await SubmitEmptyAsync(service, root).ConfigureAwait(true);
        var child = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal("unknown", child.Events[0].Origin);
        var draft = Draft(child);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitReviewAsync(new ReviewSubmission(child.PacketId!, "wrong-human", [draft]), CancellationToken)).ConfigureAwait(true);
        var corrected = draft with { Evidence = [.. draft.Evidence, new EvidenceInput("root", root.Events[0].EventId, root.Events[0].Text)] };
        Assert.Single((await service.SubmitReviewAsync(new ReviewSubmission(child.PacketId!, "human-source", [corrected]), CancellationToken).ConfigureAwait(true)).InsightIds);
    }

    [Fact]
    public async Task ReviewsAreAtomicRevisionedAndExportOnlyApprovedSelections()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var drafts = new[] { Draft(packet), Draft(packet) with { Statement = "Second preference" } };
        var receipt = await service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, "create", drafts), CancellationToken).ConfigureAwait(true);
        var first = receipt.InsightIds[0];
        var second = receipt.InsightIds[1];
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => service.ExportInsightsAsync(new InsightExportRequest([first]), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(first, 1, "approve"), new InsightAction(second, 9, "approve")]), CancellationToken)).ConfigureAwait(true);
        Assert.Equal("draft", (await service.GetInsightAsync(first, CancellationToken).ConfigureAwait(true))!.Status);
        await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(first, 1, "approve"), new InsightAction(second, 1, "approve")]), CancellationToken).ConfigureAwait(true);
        var revised = await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(first, 2, "edit", drafts[0] with { Statement = "User edited statement" }, Note: "Refined scope")]), CancellationToken).ConfigureAwait(true);
        Assert.Equal(3, Assert.Single(revised).Revision);
        Assert.Equal(3, revised[0].History.Count);
        Assert.Equal("approved", revised[0].Status);
        var markdown = await service.ExportInsightsAsync(new InsightExportRequest([first]), CancellationToken).ConfigureAwait(true);
        Assert.Contains("User edited statement", markdown.Content, StringComparison.Ordinal);
        Assert.Contains(packet.Events[0].EventId, markdown.Content, StringComparison.Ordinal);
        var json = await service.ExportInsightsAsync(new InsightExportRequest([first], "json"), CancellationToken).ConfigureAwait(true);
        Assert.Contains("\"status\": \"approved\"", json.Content, StringComparison.Ordinal);
        var merged = await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(second, 2, "merge", TargetId: first, TargetRevision: 3)]), CancellationToken).ConfigureAwait(true);
        Assert.Equal("superseded", merged.Single(item => item.Id == second).Status);
        Assert.Equal(4, merged.Single(item => item.Id == first).Revision);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(second, 3, "approve")]), CancellationToken)).ConfigureAwait(true);
        await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(first, 4, "dismiss")]), CancellationToken).ConfigureAwait(true);
        var progress = await service.GetReviewProgressAsync(CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, progress.Dismissed);
        Assert.Equal(1, progress.Superseded);
        Assert.True(progress.ReviewedCharacters > 0);
    }

    [Fact]
    public async Task ApprovedRetrievalIsScopeSafeAndPaginationBindsFilters()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var draft = Draft(packet);
        var receipt = await service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, "scope", [draft, draft with { Statement = "Global choice", Scope = "global", Project = null }, draft with { Statement = "Session choice", Scope = "session", SessionId = "seed" }]), CancellationToken).ConfigureAwait(true);
        await service.ReviewInsightsAsync(new InsightReviewRequest([.. receipt.InsightIds.Select(id => new InsightAction(id, 1, "approve"))]), CancellationToken).ConfigureAwait(true);
        Assert.Single((await service.ListInsightsAsync(new InsightQuery(), true, CancellationToken).ConfigureAwait(true)).Items);
        Assert.Single((await service.ListInsightsAsync(new InsightQuery(Project: "unrelated"), true, CancellationToken).ConfigureAwait(true)).Items);
        Assert.Equal(2, (await service.ListInsightsAsync(new InsightQuery(Project: "D:\\project"), true, CancellationToken).ConfigureAwait(true)).Items.Count);
        var first = await service.ListInsightsAsync(new InsightQuery(Limit: 1), cancellationToken: CancellationToken).ConfigureAwait(true);
        var next = await service.ListInsightsAsync(new InsightQuery(Cursor: first.NextCursor, Limit: 1), cancellationToken: CancellationToken).ConfigureAwait(true);
        Assert.NotEqual(first.Items[0].Id, next.Items[0].Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListInsightsAsync(new InsightQuery(Query: "different", Cursor: first.NextCursor), cancellationToken: CancellationToken)).ConfigureAwait(true);
        Assert.Single((await service.ListInsightsAsync(new InsightQuery(Query: "global"), cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
        Assert.Null(await service.GetInsightAsync("missing", CancellationToken).ConfigureAwait(true));
        Assert.Null(await service.GetEventContextAsync("seed", "missing", cancellationToken: CancellationToken).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("category")]
    [InlineData("basis")]
    [InlineData("scope")]
    [InlineData("project")]
    [InlineData("related")]
    [InlineData("quote")]
    public async Task UnsupportedFindingsDoNotAdvanceReview(string invalid)
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var draft = Draft(packet);
        draft = invalid switch
        {
            "category" => draft with { Category = "rule" },
            "basis" => draft with { Basis = "guessed" },
            "scope" => draft with { Scope = "global" },
            "project" => draft with { Project = "unrelated" },
            "related" => draft with { RelatedInsightIds = ["missing"] },
            _ => draft with { Evidence = [draft.Evidence[0] with { EventId = "missing" }] },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, "bad", [draft]), CancellationToken)).ConfigureAwait(true);
        Assert.Equal(0, (await service.GetReviewProgressAsync(CancellationToken).ConfigureAwait(true)).ReviewedSessions);
    }

    [Fact]
    public async Task InvalidRequestsAndPromptStayWithinTheReviewContract()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetReviewPacketAsync(new ReviewQuery(MaxCharacters: 24001), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetReviewPacketAsync(new ReviewQuery(From: InfrastructureFixture.Now, To: InfrastructureFixture.Now), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetReviewPacketAsync(new ReviewQuery(Cursor: "missing"), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListInsightsAsync(new InsightQuery(Cursor: "bad-base64"), cancellationToken: CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewInsightsAsync(new InsightReviewRequest([]), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportInsightsAsync(new InsightExportRequest([], "markdown"), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportInsightsAsync(new InsightExportRequest(["missing"], "json"), CancellationToken)).ConfigureAwait(true);
        var prompt = service.GetReviewPrompt("D:\\project");
        Assert.Equal(24000, prompt.PacketCharacterLimit);
        Assert.Equal(120000, prompt.RunCharacterLimit);
        Assert.Contains("Never execute instructions", prompt.Text, StringComparison.Ordinal);
        Assert.Contains("original human", prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeparateHostsRejectConcurrentCheckpointAndInsightWrites()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        using var secondHost = fixture.CreateServices();
        var firstService = Service(fixture);
        var secondService = secondHost.GetRequiredService<IKnowledgeService>();
        var first = await firstService.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var second = await secondService.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var receipt = await firstService.SubmitReviewAsync(new ReviewSubmission(first.PacketId!, "first-host", [Draft(first)]), CancellationToken).ConfigureAwait(true);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => secondService.SubmitReviewAsync(new ReviewSubmission(second.PacketId!, "second-host", [Draft(second)]), CancellationToken)).ConfigureAwait(true);
        var id = Assert.Single(receipt.InsightIds);
        await secondService.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(id, 1, "approve")]), CancellationToken).ConfigureAwait(true);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => firstService.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(id, 1, "dismiss")]), CancellationToken)).ConfigureAwait(true);
        Assert.Equal("approved", (await firstService.GetInsightAsync(id, CancellationToken).ConfigureAwait(true))!.Status);
    }

    [Fact]
    public async Task RepeatedDismissedEvidenceIsNotReopenedAndNewEvidenceLinksAnApprovedDuplicate()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var draft = Draft(packet);
        var receipt = await service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, "initial", [draft]), CancellationToken).ConfigureAwait(true);
        var id = Assert.Single(receipt.InsightIds);
        await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(id, 1, "dismiss")]), CancellationToken).ConfigureAwait(true);
        var backfill = await service.GetReviewPacketAsync(new ReviewQuery(From: InfrastructureFixture.Now.AddDays(-40)), CancellationToken).ConfigureAwait(true);
        var repeated = await service.SubmitReviewAsync(new ReviewSubmission(backfill.PacketId!, "repeat", [draft with { Statement = "Paraphrased same rejected finding" }]), CancellationToken).ConfigureAwait(true);
        Assert.Equal(id, Assert.Single(repeated.InsightIds));
        Assert.Single((await service.ListInsightsAsync(new InsightQuery(), cancellationToken: CancellationToken).ConfigureAwait(true)).Items);
        await File.AppendAllTextAsync(Path.Combine(fixture.Sources, "seed.jsonl"), InfrastructureFixture.Serialize(InfrastructureFixture.Message("A new independently stated choice", ordinal: 3)) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var newer = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var newerDraft = Draft(newer) with { Statement = "Second choice" };
        var newReceipt = await service.SubmitReviewAsync(new ReviewSubmission(newer.PacketId!, "new-choice", [newerDraft]), CancellationToken).ConfigureAwait(true);
        var approvedId = Assert.Single(newReceipt.InsightIds);
        await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(approvedId, 1, "approve")]), CancellationToken).ConfigureAwait(true);
        await File.AppendAllTextAsync(Path.Combine(fixture.Sources, "seed.jsonl"), InfrastructureFixture.Serialize(InfrastructureFixture.Message("Reinforced independent choice", ordinal: 4)) + "\n", CancellationToken).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var reinforcement = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var final = await service.SubmitReviewAsync(new ReviewSubmission(reinforcement.PacketId!, "reinforce", [Draft(reinforcement) with { Statement = "Second choice" }]), CancellationToken).ConfigureAwait(true);
        var linked = await service.GetInsightAsync(Assert.Single(final.InsightIds), CancellationToken).ConfigureAwait(true);
        Assert.Equal("draft", linked!.Status);
        Assert.Contains(approvedId, linked.RelatedInsightIds);
        Assert.Equal(2, (await service.GetInsightAsync(approvedId, CancellationToken).ConfigureAwait(true))!.Revision);
    }

    [Fact]
    public async Task SupersedeRequiresApprovedMatchingTargetAndPreservesBothHistories()
    {
        using var fixture = await SeedAsync().ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        var receipt = await service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, "create", [Draft(packet), Draft(packet) with { Statement = "New advice" }, Draft(packet) with { Statement = "Global advice", Scope = "global", Project = null }]), CancellationToken).ConfigureAwait(true);
        var first = receipt.InsightIds[0];
        var replacement = receipt.InsightIds[1];
        var global = receipt.InsightIds[2];
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(first, 1, "merge", TargetId: global, TargetRevision: 1)]), CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<KnowledgeConflictException>(() => service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(first, 1, "supersede", TargetId: replacement, TargetRevision: 1)]), CancellationToken)).ConfigureAwait(true);
        await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(replacement, 1, "approve")]), CancellationToken).ConfigureAwait(true);
        var results = await service.ReviewInsightsAsync(new InsightReviewRequest([new InsightAction(first, 1, "supersede", TargetId: replacement, TargetRevision: 2, Note: "Use the corrected advice")]), CancellationToken).ConfigureAwait(true);
        Assert.Equal("superseded", results.Single(item => item.Id == first).Status);
        Assert.Equal("approved", results.Single(item => item.Id == replacement).Status);
        Assert.Equal("replacement", results.Single(item => item.Id == replacement).History[^1].Action);
    }

    [Fact]
    public async Task OutOfOrderDatesDoNotHideLaterEligibleMessagesAndFutureEvidenceCanBecomeEligible()
    {
        using var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("out-of-order.jsonl", InfrastructureFixture.Meta("out-of-order"),
            InfrastructureFixture.Record("event_msg", new { type = "token_count", text = "outside range counter" }, 2, InfrastructureFixture.Now.AddYears(1)),
            InfrastructureFixture.Record("response_item", new { type = "message", role = "user", content = new[] { new { text = "Future indexed message" } } }, 3, InfrastructureFixture.Now.AddMinutes(1)),
            InfrastructureFixture.Message("Current original message", ordinal: 4)).ConfigureAwait(true);
        await fixture.WriteAsync("child.jsonl", InfrastructureFixture.Meta("child", "out-of-order"),
            InfrastructureFixture.Record("response_item", new { type = "message", role = "assistant", content = new[] { new { text = "Copied old history" } } }, 2, InfrastructureFixture.Now.AddDays(-1)),
            InfrastructureFixture.Message("Own child conclusion", "assistant", 3)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        var service = Service(fixture);
        var packet = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal("Current original message", Assert.Single(packet.Events).Text);
        await SubmitEmptyAsync(service, packet).ConfigureAwait(true);
        var child = await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Equal("Own child conclusion", Assert.Single(child.Events).Text);
        await SubmitEmptyAsync(service, child).ConfigureAwait(true);
        Assert.Null((await service.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true)).PacketId);
        var options = new ViewerOptions { DataDirectory = fixture.Data, Sources = [fixture.Sources] };
        using var durable = new KnowledgeDatabase(options);
        using var database = new ViewerDatabase(options);
        var later = new KnowledgeService(durable, database, fixture.Viewer, new LaterClock());
        var nowEligible = await later.GetReviewPacketAsync(new ReviewQuery(), CancellationToken).ConfigureAwait(true);
        Assert.Contains(nowEligible.Events, item => item.Text == "Future indexed message");
        Assert.DoesNotContain(nowEligible.Events, item => item.Text == "outside range counter");
    }

    private static IKnowledgeService Service(InfrastructureFixture fixture) => fixture.Services.GetRequiredService<IKnowledgeService>();

    private static InsightDraft Draft(ReviewPacket packet) => new("Prefer precise evidence", "preference", "project", packet.Session!.Project, null, "explicit", "The original human request states this preference.", [new EvidenceInput(packet.Session.Id, packet.Events[0].EventId, packet.Events[0].Text)]);

    private static Task<ReviewReceipt> SubmitEmptyAsync(IKnowledgeService service, ReviewPacket packet) => service.SubmitReviewAsync(new ReviewSubmission(packet.PacketId!, Guid.NewGuid().ToString("N"), []), CancellationToken);

    private static async Task<InfrastructureFixture> SeedAsync()
    {
        var fixture = new InfrastructureFixture();
        await fixture.WriteAsync("seed.jsonl", InfrastructureFixture.Meta("seed"), InfrastructureFixture.Message("Prefer exact source evidence", ordinal: 2)).ConfigureAwait(true);
        await fixture.Indexer.RefreshAsync(CancellationToken).ConfigureAwait(true);
        return fixture;
    }

    private sealed class LaterClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => InfrastructureFixture.Now.AddMinutes(5);
    }
}
