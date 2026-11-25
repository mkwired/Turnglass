using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Turnglass.Core;
using Turnglass.Web.Middleware;

namespace Turnglass.Tests;

public sealed class HostTests
{
    [Fact]
    public async Task ApiReturnsUsageAndForwardsFilters()
    {
        var viewer = new HostFakeViewer();
        using var factory = new HostApplicationFactory(viewer);
        using var client = factory.CreateClient();
        var report = await client.GetFromJsonAsync<UsageReport>("/api/usage?mode=rolling&timezone=UTC&project=sample&model=test&from=2026-11-01T00:00:00Z&to=2026-11-21T00:00:00Z", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotNull(report);
        Assert.Equal(110, report.Periods[0].Totals.Tokens.TotalTokens);
        Assert.Equal("rolling", viewer.LastUsageQuery?.Mode);
        Assert.Equal("sample", viewer.LastUsageQuery?.Project);
        Assert.Equal("test", viewer.LastUsageQuery?.Model);
        Assert.NotNull(viewer.LastUsageQuery?.From);
        Assert.Equal("UTC", report.Timezone);
        Assert.True(report.Index.InitialIndexComplete);
    }

    [Fact]
    public async Task OmittedQueryParametersUseDefaultFilters()
    {
        var viewer = new HostFakeViewer();
        using var factory = new HostApplicationFactory(viewer);
        using var client = factory.CreateClient();
        using var usage = await client.GetAsync("/api/usage", TestContext.Current.CancellationToken).ConfigureAwait(true);
        usage.EnsureSuccessStatusCode();
        Assert.Equal(new UsageQuery(), viewer.LastUsageQuery);
        using var sessions = await client.GetAsync("/api/sessions", TestContext.Current.CancellationToken).ConfigureAwait(true);
        sessions.EnsureSuccessStatusCode();
        Assert.Equal(new SessionFilter(), viewer.LastFilter);
    }

    [Theory]
    [InlineData("/api/usage?from=invalid")]
    [InlineData("/api/sessions?limit=invalid")]
    [InlineData("/api/sessions/session-a/events?limit=invalid")]
    [InlineData("/api/sessions/session-a/records/event-a?offset=invalid")]
    [InlineData("/api/search?query=phrase&limit=invalid")]
    [InlineData("/api/search")]
    public async Task InvalidOrMissingQueryParametersReturnBadRequest(string path)
    {
        using var factory = new HostApplicationFactory(new HostFakeViewer());
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SessionsSupportFilteringAndDetail()
    {
        var viewer = new HostFakeViewer();
        using var factory = new HostApplicationFactory(viewer);
        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<PageResult<SessionView>>("/api/sessions?query=synthetic&project=sample&model=test&cursor=next&limit=5&from=2026-11-01T00:00:00Z&to=2026-11-21T00:00:00Z", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Single(Assert.IsType<PageResult<SessionView>>(page).Items);
        Assert.Equal("synthetic", viewer.LastFilter?.Query);
        Assert.Equal("next", viewer.LastFilter?.Cursor);
        Assert.Equal(5, viewer.LastFilter?.Limit);
        var detail = await client.GetFromJsonAsync<SessionDetail>("/api/sessions/session-a", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("session-a", detail?.Detail.Session.Id);
        viewer.Missing = true;
        using var missing = await client.GetAsync("/api/sessions/missing", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task LogsReturnEscapedJsonAndSupportRecordChunks()
    {
        var viewer = new HostFakeViewer();
        using var factory = new HostApplicationFactory(viewer);
        using var client = factory.CreateClient();
        using var events = await client.GetAsync("/api/sessions/session-a/events?cursor=next&limit=10", TestContext.Current.CancellationToken).ConfigureAwait(true);
        var text = await events.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.DoesNotContain("<script>", text, StringComparison.Ordinal);
        Assert.Equal("application/json", events.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", events.Headers.GetValues("X-Content-Type-Options").Single());
        var chunk = await client.GetFromJsonAsync<LogChunk>("/api/sessions/session-a/records/event-a?offset=7&limit=10", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("event-a", chunk?.EventId);
        Assert.Equal(7, viewer.LastOffset);
        viewer.Missing = true;
        using var missing = await client.GetAsync("/api/sessions/session-a/records/missing", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SearchSettingsAndIndexEndpointsWork()
    {
        var viewer = new HostFakeViewer();
        using var factory = new HostApplicationFactory(viewer);
        using var client = factory.CreateClient();
        var search = await client.GetFromJsonAsync<PageResult<EventView>>("/api/search?query=phrase&sessionId=session-a&cursor=next&limit=2", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Single(Assert.IsType<PageResult<EventView>>(search).Items);
        Assert.Equal("phrase", viewer.LastSearch);
        Assert.Equal("session-a", viewer.LastSessionId);
        var filters = await client.GetFromJsonAsync<FilterOptions>("/api/filters", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Contains("sample", Assert.IsType<FilterOptions>(filters).Projects);
        var status = await client.GetFromJsonAsync<IndexStatus>("/api/index", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(status?.InitialIndexComplete);
        var settings = await client.GetFromJsonAsync<ViewerSettings>("/api/settings", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("America/New_York", settings?.Timezone);
        using var update = await client.PutAsJsonAsync("/api/settings", viewer.Settings with { Timezone = "UTC" }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        update.EnsureSuccessStatusCode();
        Assert.Equal("UTC", viewer.Settings.Timezone);
        using var rebuild = await client.PostAsync("/api/index/rebuild", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        rebuild.EnsureSuccessStatusCode();
        Assert.True(viewer.Rebuilt);
    }

    [Fact]
    public async Task SettingsRejectQuotedJsonNumbers()
    {
        var viewer = new HostFakeViewer();
        var originalSettings = viewer.Settings;
        var settings = JsonSerializer.SerializeToNode(originalSettings, JsonSerializerOptions.Web)!;
        settings["pricing"]!["rates"]![0]!["inputPerMillion"] = "1.0";
        using var factory = new HostApplicationFactory(viewer);
        using var client = factory.CreateClient();
        using var response = await client.PutAsJsonAsync("/api/settings", settings, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Same(originalSettings, viewer.Settings);
    }

    [Theory]
    [InlineData("https://evil.example", null)]
    [InlineData("http://127.0.0.1:9999", null)]
    [InlineData("null", null)]
    [InlineData(null, "evil.example")]
    public async Task RejectsUntrustedOriginsAndHostnames(string? origin, string? hostname)
    {
        using var factory = new HostApplicationFactory(new HostFakeViewer());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/rebuild");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (hostname is not null)
        {
            request.Headers.Host = hostname;
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AcceptsSameOriginAndDevelopmentViteButRejectsCrossSite()
    {
        using var factory = new HostApplicationFactory(new HostFakeViewer(), true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var sameOrigin = await client.GetAsync("/api/index", TestContext.Current.CancellationToken).ConfigureAwait(true);
        sameOrigin.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var vite = await client.GetAsync("/api/index", TestContext.Current.CancellationToken).ConfigureAwait(true);
        vite.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        using var rejected = await client.GetAsync("/api/index", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
    }

    [Theory]
    [InlineData("Turnglass:FrontendOrigin", true, HttpStatusCode.OK)]
    [InlineData("Turnglass:FrontendOrigin", false, HttpStatusCode.Forbidden)]
    [InlineData("SessionViewer:FrontendOrigin", true, HttpStatusCode.Forbidden)]
    public async Task CustomDevelopmentOriginUsesTurnglassConfiguration(string setting, bool development, HttpStatusCode expected)
    {
        using var factory = new HostApplicationFactory(new HostFakeViewer(), development);
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting(setting, "http://localhost:5317"));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5317");
        using var response = await client.GetAsync("/api/index", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiPublishesTypedContractsAndHealthRoutes()
    {
        using var factory = new HostApplicationFactory(new HostFakeViewer());
        using var client = factory.CreateClient();
        using var schema = await client.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotNull(schema);
        var paths = schema.RootElement.GetProperty("paths");
        var routes = new (string Path, string Method)[]
        {
            ("/api/usage", "get"),
            ("/api/sessions", "get"),
            ("/api/sessions/{id}", "get"),
            ("/api/sessions/{id}/events", "get"),
            ("/api/sessions/{id}/records/{eventId}", "get"),
            ("/api/search", "get"),
            ("/api/filters", "get"),
            ("/api/index", "get"),
            ("/api/settings", "get"),
            ("/api/settings", "put"),
            ("/api/index/rebuild", "post")
        };
        foreach (var (path, method) in routes)
        {
            Assert.True(paths.GetProperty(path).TryGetProperty(method, out _));
        }

        var lookups = new (string Path, string Schema)[]
        {
            ("/api/sessions/{id}", "SessionDetail"),
            ("/api/sessions/{id}/records/{eventId}", "LogChunk")
        };
        foreach (var (path, expectedSchema) in lookups)
        {
            var responses = paths.GetProperty(path).GetProperty("get").GetProperty("responses");
            var responseSchema = responses.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.Equal("#/components/schemas/" + expectedSchema, responseSchema.GetProperty("$ref").GetString());
            Assert.True(responses.TryGetProperty("404", out _));
        }

        Assert.True(schema.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("UsageReport", out _));
        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken).ConfigureAwait(true);
        health.EnsureSuccessStatusCode();
        using var live = await client.GetAsync("/alive", TestContext.Current.CancellationToken).ConfigureAwait(true);
        live.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("argument", 400)]
    [InlineData("timezone", 400)]
    [InlineData("io", 503)]
    [InlineData("permission", 503)]
    [InlineData("initializing", 503)]
    public async Task MapsValidationAndIndexFailuresToProblemDetails(string failure, int expected)
    {
        var exception = failure switch
        {
            "argument" => (Exception)new ArgumentException("Bad query"),
            "timezone" => new TimeZoneNotFoundException(),
            "io" => new IOException("Private source path"),
            "permission" => new UnauthorizedAccessException("Private source path"),
            _ => new InvalidOperationException("Initializing")
        };
        using var factory = new HostApplicationFactory(new HostFakeViewer { Failure = exception });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/index", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Private source path", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("localhost.evil.example", false)]
    public void OnlyLoopbackHostsAreAllowed(string host, bool allowed)
    {
        Assert.Equal(allowed, LocalRequestMiddleware.IsLoopbackHost(host));
    }
}
