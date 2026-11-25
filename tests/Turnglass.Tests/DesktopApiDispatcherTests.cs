using System.Net;
using System.Text;
using System.Text.Json;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class DesktopApiDispatcherTests
{
    private const string SettingsBody = """{"sources":[],"timezone":"UTC","pricing":{"version":"fixture","sourceUrl":"https://example.test","rates":[],"modelAliases":{},"tierOverrides":{},"cacheWriteLifetimeOverrides":{}}}""";

    public static TheoryData<string, string, string?> Operations => new()
    {
        { "GET", "/usage?mode=rolling&timezone=UTC&project=sample&model=test&from=2026-11-01T00:00:00Z&to=2026-11-21T00:00:00Z", null },
        { "GET", "/sessions?query=synthetic&project=sample&model=test&cursor=next&limit=5&from=2026-11-01T00:00:00Z&to=2026-11-21T00:00:00Z", null },
        { "GET", "/sessions/session-a", null },
        { "GET", "/sessions/session-a/events?cursor=next&limit=7", null },
        { "GET", "/sessions/session-a/records/event-a?offset=4&limit=100", null },
        { "GET", "/search?query=narrow+tests&sessionId=session-a&cursor=next&limit=8", null },
        { "GET", "/filters", null },
        { "GET", "/index", null },
        { "GET", "/settings", null },
        { "PUT", "/settings", SettingsBody },
        { "POST", "/index/rebuild", null },
        { "GET", "/insights?query=narrow&project=sample&category=preference&status=draft&cursor=after&limit=5", null },
        { "GET", "/insights/insight-a", null },
        { "POST", "/insights/review", """{"actions":[{"id":"insight-a","expectedRevision":1,"action":"approve"}]}""" },
        { "POST", "/insights/export", """{"ids":["insight-a"],"format":"markdown"}""" },
        { "GET", "/reviews/progress", null },
        { "GET", "/reviews/prompt?project=sample", null },
        { "GET", "/sessions/session-a/events/event-a/context?before=1&after=2&maxCharacters=1000", null },
        { "GET", "/copilot/usage?mode=rolling&timezone=UTC&client=shared&model=gpt-5.4&from=2026-11-01T00:00:00Z&to=2026-11-21T00:00:00Z", null },
        { "GET", "/copilot/index", null },
        { "GET", "/copilot/filters", null },
        { "GET", "/copilot/settings", null },
        { "PUT", "/copilot/settings", """{"autoDiscover":false,"sources":[{"kind":"shared","path":"C:\\fixture"}]}""" },
        { "POST", "/copilot/index/rescan", null },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EveryDesktopOperationMatchesItsHttpResponse(string method, string path, string? body)
    {
        var viewer = new HostFakeViewer();
        var knowledge = new HostFakeKnowledge();
        var copilot = new HostFakeCopilot();
        var dispatcher = new DesktopApiDispatcher(viewer, knowledge, copilot);
        using var factory = new HostApplicationFactory(new HostFakeViewer(), knowledge: new HostFakeKnowledge(), copilot: new HostFakeCopilot());
        using var client = factory.CreateClient();
        using var httpRequest = new HttpRequestMessage(new HttpMethod(method), "/api" + path);
        if (body is not null)
        {
            httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var httpResponse = await client.SendAsync(httpRequest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var response = await InvokeAsync(dispatcher, method, path, body).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
        Assert.Equal((int)httpResponse.StatusCode, response.Status);
        Assert.Equal(1, response.Version);
        Assert.Equal("request-one", response.Id);
        using var httpPayload = JsonDocument.Parse(await httpResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        using var desktopPayload = JsonDocument.Parse(Assert.IsType<string>(response.Body));
        Assert.True(JsonElement.DeepEquals(httpPayload.RootElement, desktopPayload.RootElement), $"Different payloads for {method} {path}");
        if (path == "/index/rebuild")
        {
            Assert.True(viewer.Rebuilt);
        }

        if (path == "/copilot/index/rescan")
        {
            Assert.True(copilot.Rescanned);
        }
    }

    [Fact]
    public async Task PreservesDefaultsAndDecodedQueryAndIdentifierValues()
    {
        var viewer = new HostFakeViewer();
        var knowledge = new HostFakeKnowledge();
        var copilot = new HostFakeCopilot();
        var dispatcher = new DesktopApiDispatcher(viewer, knowledge, copilot);
        Assert.Equal(200, (await InvokeAsync(dispatcher, "GET", "/usage").ConfigureAwait(true)).Status);
        Assert.Equal(new UsageQuery(), viewer.LastUsageQuery);
        Assert.Equal(200, (await InvokeAsync(dispatcher, "GET", "/sessions").ConfigureAwait(true)).Status);
        Assert.Equal(new SessionFilter(), viewer.LastFilter);
        Assert.Equal(200, (await InvokeAsync(dispatcher, "GET", "/insights").ConfigureAwait(true)).Status);
        Assert.Equal(new InsightQuery(), knowledge.LastQuery);
        Assert.False(knowledge.ApprovedOnly);
        Assert.Equal(200, (await InvokeAsync(dispatcher, "GET", "/copilot/usage").ConfigureAwait(true)).Status);
        Assert.Equal(new CopilotUsageQuery(), copilot.LastQuery);
        Assert.Equal(200, (await InvokeAsync(dispatcher, "GET", "/sessions/session%20%2F%20one/events/event%20%2F%20one/context").ConfigureAwait(true)).Status);
        Assert.Equal(("session / one", "event / one", 3, 3, 16000), knowledge.LastContext);
        Assert.Equal(200, (await InvokeAsync(dispatcher, "GET", "/search?query=one+two%2Bthree&sessionId=session%2Fa").ConfigureAwait(true)).Status);
        Assert.Equal("one two+three", viewer.LastSearch);
        Assert.Equal("session/a", viewer.LastSessionId);
        Assert.Equal(200, (await InvokeAsync(dispatcher, "GET", "/reviews/prompt?project=D%3A%5Csource%5Csample").ConfigureAwait(true)).Status);
        Assert.Equal("D:\\source\\sample", knowledge.LastProject);
    }

    [Theory]
    [InlineData("/sessions/missing")]
    [InlineData("/sessions/missing/records/event-a")]
    [InlineData("/insights/missing")]
    [InlineData("/sessions/missing/events/event-a/context")]
    public async Task MissingRecordsReturnNotFound(string path)
    {
        var dispatcher = new DesktopApiDispatcher(new HostFakeViewer { Missing = true }, new HostFakeKnowledge { Missing = true }, new HostFakeCopilot());
        AssertProblem(await InvokeAsync(dispatcher, "GET", path).ConfigureAwait(true), 404, "Not found");
    }

    [Theory]
    [InlineData("GET", "/health", 404)]
    [InlineData("GET", "/api/index", 404)]
    [InlineData("GET", "/insights/submit", 200)]
    [InlineData("POST", "/reviews/submit", 404)]
    [InlineData("POST", "/usage", 405)]
    [InlineData("PUT", "/index/rebuild", 405)]
    [InlineData("POST", "/sessions/a/events", 405)]
    [InlineData("PUT", "/insights/a", 405)]
    [InlineData("POST", "/copilot/settings", 405)]
    [InlineData("GET", "/copilot/index/rescan", 405)]
    [InlineData("DELETE", "/settings", 400)]
    public async Task OnlyExplicitRoutesAndMethodsAreAvailable(string method, string path, int expectedStatus)
    {
        var response = await InvokeAsync(Create(), method, path).ConfigureAwait(true);
        Assert.Equal(expectedStatus, response.Status);
    }

    [Theory]
    [InlineData("https://turnglass.local/index")]
    [InlineData("//index")]
    [InlineData("/../index")]
    [InlineData("/sessions/%2E%2E")]
    [InlineData("/sessions/a%2F..%2Fb")]
    [InlineData("/sessions/%5Cprivate")]
    [InlineData("/sessions//events")]
    [InlineData("/index#fragment")]
    [InlineData("/index\\file")]
    [InlineData("/sessions/%00")]
    [InlineData("/sessions/%GG")]
    [InlineData("/usage?model=%")]
    [InlineData("/usage?=unknown")]
    [InlineData("/usage?model=a&MODEL=b")]
    [InlineData("/usage?model=%00")]
    [InlineData("/usage?from=not-a-date")]
    [InlineData("/copilot/usage?to=not-a-date")]
    [InlineData("/sessions?limit=invalid")]
    [InlineData("/sessions?limit=2147483648")]
    [InlineData("/sessions?limit=1.5")]
    [InlineData("/sessions?limit=")]
    [InlineData("/sessions/a/events?limit=no")]
    [InlineData("/sessions/a/records/b?offset=no")]
    [InlineData("/search?query=text&limit=no")]
    [InlineData("/search")]
    [InlineData("/search?query=+")]
    [InlineData("/insights?limit=no")]
    [InlineData("/sessions/a/events/b/context?before=no")]
    [InlineData("/sessions/a/events/b/context?after=no")]
    [InlineData("/sessions/a/events/b/context?maxCharacters=no")]
    public async Task MalformedPathsAndQueriesFailBeforeServiceCalls(string path)
    {
        var viewer = new HostFakeViewer();
        var response = await InvokeAsync(new(viewer, new HostFakeKnowledge(), new HostFakeCopilot()), "GET", path).ConfigureAwait(true);
        AssertProblem(response, 400, "Invalid request");
        Assert.Null(viewer.LastSessionId);
        Assert.Null(viewer.LastFilter);
        Assert.Null(viewer.LastUsageQuery);
        Assert.Null(viewer.LastSearch);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"version":2,"id":"test","method":"GET","path":"/index","body":null}""")]
    [InlineData("""{"version":"1","id":"test","method":"GET","path":"/index","body":null}""")]
    [InlineData("""{"version":1,"id":"","method":"GET","path":"/index","body":null}""")]
    [InlineData("""{"version":1,"id":3,"method":"GET","path":"/index","body":null}""")]
    [InlineData("""{"version":1,"id":"test","path":"/index","body":null}""")]
    [InlineData("""{"version":1,"id":"test","method":"GET","path":"/index"}""")]
    [InlineData("""{"version":1,"id":"test","method":"GET","path":null,"body":null}""")]
    [InlineData("""{"version":1,"id":"test","method":"GET","path":"/index","body":null,"extra":1}""")]
    [InlineData("""{"version":1,"id":"test","id":"duplicate","method":"GET","path":"/index","body":null}""")]
    [InlineData("""{"version":1,"id":"test","method":"GET","path":"/index","body":{}}""")]
    public async Task InvalidEnvelopesReturnSafeErrors(string request)
    {
        var response = JsonSerializer.Deserialize(await Create().InvokeAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true), DesktopJsonContext.Default.DesktopResponse);
        AssertProblem(Assert.IsType<DesktopResponse>(response), 400, "Invalid request");
    }

    [Theory]
    [InlineData("PUT", "/settings", null)]
    [InlineData("PUT", "/settings", "null")]
    [InlineData("PUT", "/settings", "{}")]
    [InlineData("PUT", "/settings", "{invalid")]
    [InlineData("POST", "/insights/review", """{"actions":[{"id":"one","expectedRevision":"1","action":"approve"}]}""")]
    [InlineData("POST", "/insights/review", """{"actions":null}""")]
    [InlineData("POST", "/insights/export", """{"ids":null}""")]
    [InlineData("PUT", "/copilot/settings", """{"autoDiscover":"false","sources":[]}""")]
    [InlineData("PUT", "/copilot/settings", """{"autoDiscover":false,"sources":[],"extra":1}""")]
    [InlineData("GET", "/index", "{}")]
    [InlineData("POST", "/index/rebuild", "{}")]
    [InlineData("POST", "/copilot/index/rescan", "{}")]
    public async Task BodiesUseStrictJsonContracts(string method, string path, string? body)
    {
        AssertProblem(await InvokeAsync(Create(), method, path, body).ConfigureAwait(true), 400, "Invalid request");
    }

    [Fact]
    public async Task RejectsQuotedPricingNumbersAndOversizedEnvelopes()
    {
        var body = SettingsBody.Replace("\"rates\":[]", "\"rates\":[{\"model\":\"m\",\"tier\":\"standard\",\"minInputTokens\":0,\"maxInputTokens\":null,\"inputPerMillion\":\"1\",\"cachedInputPerMillion\":0,\"outputPerMillion\":1}]", StringComparison.Ordinal);
        AssertProblem(await InvokeAsync(Create(), "PUT", "/settings", body).ConfigureAwait(true), 400, "Invalid request");
        var oversized = new string(' ', 1_048_577);
        var response = JsonSerializer.Deserialize(await Create().InvokeAsync(oversized, TestContext.Current.CancellationToken).ConfigureAwait(true), DesktopJsonContext.Default.DesktopResponse);
        AssertProblem(Assert.IsType<DesktopResponse>(response), 400, "Invalid request");
    }

    [Theory]
    [InlineData("argument", 400, "Invalid request")]
    [InlineData("timezone", 400, "Invalid timezone")]
    [InlineData("conflict", 409, "Review conflict")]
    [InlineData("missing", 404, "Not found")]
    [InlineData("io", 503, "Index unavailable")]
    [InlineData("access", 503, "Index unavailable")]
    [InlineData("invalid", 503, "Index unavailable")]
    [InlineData("timeout", 503, "Index unavailable")]
    [InlineData("sqlite", 503, "Index unavailable")]
    [InlineData("unexpected", 500, "Request failed")]
    public async Task ServiceErrorsUseTheHttpStatusContractWithoutLeakingPrivateFailures(string kind, int status, string title)
    {
        var failure = kind switch
        {
            "argument" => (Exception)new ArgumentException("Select valid settings."),
            "timezone" => new TimeZoneNotFoundException("private-path"),
            "conflict" => new KnowledgeConflictException("Refresh the insight."),
            "missing" => new KeyNotFoundException("private-path"),
            "io" => new IOException("private-path"),
            "access" => new UnauthorizedAccessException("private-path"),
            "invalid" => new InvalidOperationException("private-path"),
            "timeout" => new TimeoutException("private-path"),
            "sqlite" => new Microsoft.Data.Sqlite.SqliteException("private-path", 5),
            _ => new NotSupportedException("private-path"),
        };
        var dispatcher = new DesktopApiDispatcher(new HostFakeViewer { Failure = failure }, new HostFakeKnowledge(), new HostFakeCopilot());
        var response = await InvokeAsync(dispatcher, "GET", "/index").ConfigureAwait(true);
        AssertProblem(response, status, title);
        Assert.DoesNotContain("private-path", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingAnErrorEnvelope()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().InvokeAsync(Request("GET", "/index"), canceled.Token)).ConfigureAwait(true);
        var dispatcher = new DesktopApiDispatcher(new HostFakeViewer { Failure = new OperationCanceledException() }, new HostFakeKnowledge(), new HostFakeCopilot());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(dispatcher, "GET", "/index")).ConfigureAwait(true);
    }

    private static DesktopApiDispatcher Create() => new(new HostFakeViewer(), new HostFakeKnowledge(), new HostFakeCopilot());

    private static string Request(string method, string path, string? body = null) => JsonSerializer.Serialize(new { version = 1, id = "request-one", method, path, body });

    private static async Task<DesktopResponse> InvokeAsync(DesktopApiDispatcher dispatcher, string method, string path, string? body = null)
    {
        var json = await dispatcher.InvokeAsync(Request(method, path, body), TestContext.Current.CancellationToken).ConfigureAwait(false);
        return Assert.IsType<DesktopResponse>(JsonSerializer.Deserialize(json, DesktopJsonContext.Default.DesktopResponse));
    }

    private static void AssertProblem(DesktopResponse response, int status, string title)
    {
        Assert.Equal(status, response.Status);
        using var body = JsonDocument.Parse(Assert.IsType<string>(response.Body));
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(title, body.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));
    }
}
