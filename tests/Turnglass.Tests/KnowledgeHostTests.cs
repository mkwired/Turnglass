using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Turnglass.Core;

namespace Turnglass.Tests;

public sealed class KnowledgeHostTests
{
    [Fact]
    public async Task InsightsExposeDraftsAndForwardFilters()
    {
        var knowledge = new HostFakeKnowledge();
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: knowledge);
        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<InsightPage>("/api/insights?query=narrow&project=sample&category=preference&status=draft&cursor=after&limit=5", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("draft", Assert.Single(Assert.IsType<InsightPage>(page).Items).Status);
        Assert.False(knowledge.ApprovedOnly);
        Assert.Equal(new InsightQuery("narrow", "sample", "preference", "draft", "after", 5), knowledge.LastQuery);
        var detail = await client.GetFromJsonAsync<Insight>("/api/insights/insight-a", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("insight-a", detail?.Id);
        knowledge.Missing = true;
        using var missing = await client.GetAsync("/api/insights/missing", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ReviewAndExportForwardVersionedActionsAndExplicitSelections()
    {
        var knowledge = new HostFakeKnowledge();
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: knowledge);
        using var client = factory.CreateClient();
        var request = new InsightReviewRequest([new("insight-a", 1, "approve", Note: "Checked evidence")]);
        using var response = await client.PostAsJsonAsync("/api/insights/review", request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        var reviewed = await response.Content.ReadFromJsonAsync<Insight[]>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("approved", Assert.Single(Assert.IsType<Insight[]>(reviewed)).Status);
        Assert.Equal(request.Actions[0], Assert.Single(Assert.IsType<InsightReviewRequest>(knowledge.LastReview).Actions));
        using var exported = await client.PostAsJsonAsync("/api/insights/export", new InsightExportRequest(["insight-a"]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        exported.EnsureSuccessStatusCode();
        var export = await exported.Content.ReadFromJsonAsync<InsightExport>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("insights.md", export?.FileName);
        Assert.Equal("text/markdown", export?.ContentType);
        Assert.Equal("insight-a", Assert.Single(Assert.IsType<InsightExportRequest>(knowledge.LastExport).Ids));
    }

    [Fact]
    public async Task ProgressPromptAndBoundedEvidenceContextAreAvailable()
    {
        var knowledge = new HostFakeKnowledge();
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: knowledge);
        using var client = factory.CreateClient();
        var progress = await client.GetFromJsonAsync<ReviewProgress>("/api/reviews/progress", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(1000, progress?.ReviewedCharacters);
        var prompt = await client.GetFromJsonAsync<ReviewPrompt>("/api/reviews/prompt?project=sample", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("sample", knowledge.LastProject);
        Assert.Equal(120000, prompt?.RunCharacterLimit);
        var context = await client.GetFromJsonAsync<EventContext>("/api/sessions/session-a/events/event-a/context?before=1&after=2&maxCharacters=1000", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Single(Assert.IsType<EventContext>(context).Events);
        Assert.Equal(("session-a", "event-a", 1, 2, 1000), knowledge.LastContext);
        knowledge.Missing = true;
        using var missing = await client.GetAsync("/api/sessions/session-a/events/missing/context", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("/api/insights?limit=invalid")]
    [InlineData("/api/sessions/session-a/events/event-a/context?before=invalid")]
    public async Task InvalidNumericQueryBindingReturnsBadRequest(string path)
    {
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: new HostFakeKnowledge());
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedActionsAndCrossOriginWritesCannotReachService()
    {
        var knowledge = new HostFakeKnowledge();
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: knowledge);
        using var client = factory.CreateClient();
        using var malformed = await client.PostAsJsonAsync("/api/insights/review", new { actions = new[] { new { id = "insight-a", expectedRevision = "1", action = "approve" } } }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Null(knowledge.LastReview);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using var forbidden = await client.PostAsJsonAsync("/api/insights/review", new InsightReviewRequest([new("insight-a", 1, "approve")]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Null(knowledge.LastReview);
    }

    [Theory]
    [InlineData("conflict", 409)]
    [InlineData("validation", 400)]
    [InlineData("missing", 404)]
    [InlineData("busy", 503)]
    public async Task ReviewFailuresReturnActionableProblemDetails(string kind, int expectedStatus)
    {
        var failure = kind switch
        {
            "conflict" => (Exception)new KnowledgeConflictException("Revision changed; refresh the insight."),
            "validation" => new ArgumentException("Unknown action."),
            "missing" => new KeyNotFoundException("Private source path"),
            _ => new IOException("Private source path")
        };
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: new HostFakeKnowledge { Failure = failure });
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/insights/review", new InsightReviewRequest([new("insight-a", 1, "approve")]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.DoesNotContain("Private source path", text, StringComparison.Ordinal);
        if (kind == "conflict")
        {
            Assert.Contains("Revision changed", text, StringComparison.Ordinal);
        }

        if (kind == "busy")
        {
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        }
    }

    [Fact]
    public async Task OpenApiPublishesTypedInsightContracts()
    {
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: new HostFakeKnowledge());
        using var client = factory.CreateClient();
        using var schema = await client.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", TestContext.Current.CancellationToken).ConfigureAwait(true);
        var paths = Assert.IsType<JsonDocument>(schema).RootElement.GetProperty("paths");
        var routes = new (string Path, string Method, string Schema)[]
        {
            ("/api/insights", "get", "InsightPage"),
            ("/api/insights/{id}", "get", "Insight"),
            ("/api/insights/export", "post", "InsightExport"),
            ("/api/reviews/progress", "get", "ReviewProgress"),
            ("/api/reviews/prompt", "get", "ReviewPrompt"),
            ("/api/sessions/{sessionId}/events/{eventId}/context", "get", "EventContext")
        };
        foreach (var (path, method, expectedSchema) in routes)
        {
            var responseSchema = paths.GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.Equal("#/components/schemas/" + expectedSchema, responseSchema.GetProperty("$ref").GetString());
        }

        var review = paths.GetProperty("/api/insights/review").GetProperty("post");
        Assert.True(review.GetProperty("responses").TryGetProperty("409", out _));
        Assert.Equal("#/components/schemas/InsightReviewRequest", review.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var schemas = schema.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("integer", schemas.GetProperty("Insight").GetProperty("properties").GetProperty("revision").GetProperty("type").GetString());
        Assert.Equal("integer", schemas.GetProperty("InsightAction").GetProperty("properties").GetProperty("expectedRevision").GetProperty("type").GetString());
        foreach (var property in schemas.GetProperty("TokenUsage").GetProperty("properties").EnumerateObject())
        {
            Assert.Equal("integer", property.Value.GetProperty("type").GetString());
        }
    }
}
