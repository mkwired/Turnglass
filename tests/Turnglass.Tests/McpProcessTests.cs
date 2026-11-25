using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

public sealed class McpProcessTests
{
    [Fact]
    public async Task StdioStartsWithoutWebAndIndexesAcrossConcurrentProcesses()
    {
        var root = Path.Combine(Path.GetTempPath(), "Turnglass-mcp-" + Guid.NewGuid().ToString("N"));
        var codex = Path.Combine(root, "codex");
        var sessions = Path.Combine(codex, "sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(Path.Combine(codex, "archived_sessions"));
        var data = Path.Combine(root, "data");
        var timestamp = DateTimeOffset.UtcNow;
        var lines = new object[]
        {
            new { timestamp, type = "session_meta", payload = new { id = "stdio-session", cwd = "synthetic-project" } },
            new { timestamp, type = "turn_context", payload = new { turn_id = "turn-a", model = "gpt-6-astra" } },
            new { timestamp, type = "event_msg", payload = new { type = "thread_settings_applied", thread_id = "stdio-session", thread_settings = new { model = "gpt-6-astra", service_tier = "default" } } },
            new { timestamp, type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Synthetic stdio fixture" } } } },
            new { timestamp, type = "token_usage_record", payload = new { response_id = "response-a", thread_id = "stdio-session", turn_id = "turn-a", usage = new { input_tokens = 100, cached_input_tokens = 20, output_tokens = 10 } } }
        };
        await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout.jsonl"), lines.Select(line => JsonSerializer.Serialize(line)), TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            using var first = McpProcess.Start(data, codex);
            using var second = McpProcess.Start(data, codex);
            await first.InitializeAsync().ConfigureAwait(true);
            await second.InitializeAsync().ConfigureAwait(true);
            using var discovery = await first.RequestAsync("tools/list", new { }).ConfigureAwait(true);
            var tools = discovery.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Equal(13, tools.GetArrayLength());
            foreach (var tool in tools.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString();
                var annotations = tool.GetProperty("annotations");
                Assert.Equal(name != "submit_review", annotations.GetProperty("readOnlyHint").GetBoolean());
                Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
                Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
                Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
            }

            await first.WaitForIndexAsync().ConfigureAwait(true);
            await second.WaitForIndexAsync().ConfigureAwait(true);
            using var listed = await second.RequestAsync("tools/call", new { name = "list_sessions", arguments = new { } }).ConfigureAwait(true);
            var payload = listed.RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Single(payload.GetProperty("items").EnumerateArray());
            Assert.Equal(110, payload.GetProperty("items")[0].GetProperty("ownUsage").GetProperty("tokens").GetProperty("totalTokens").GetInt64());
            using var events = await first.RequestAsync("tools/call", new { name = "read_session_events", arguments = new { sessionId = "stdio-session" } }).ConfigureAwait(true);
            Assert.False(events.RootElement.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean());
            await VerifyReviewAcrossProcessesAsync(first, second, data).ConfigureAwait(true);
            await first.CloseAsync().ConfigureAwait(true);
            using var status = await second.RequestAsync("tools/call", new { name = "get_index_status", arguments = new { } }).ConfigureAwait(true);
            Assert.Equal(1, status.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("sessionCount").GetInt32());
            await second.CloseAsync().ConfigureAwait(true);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task VerifyReviewAcrossProcessesAsync(McpProcess first, McpProcess second, string dataDirectory)
    {
        using var packetResponse = await first.RequestAsync("tools/call", new { name = "get_review_packet", arguments = new { project = "synthetic-project", maxCharacters = 24000 } }).ConfigureAwait(true);
        var packet = packetResponse.RootElement.GetProperty("result").GetProperty("structuredContent");
        var packetId = packet.GetProperty("packetId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(packetId));
        var source = packet.GetProperty("events").EnumerateArray().Single(item => item.GetProperty("text").GetString()?.Contains("Synthetic stdio fixture", StringComparison.Ordinal) == true);
        var eventId = source.GetProperty("eventId").GetString();
        using var context = await second.RequestAsync("tools/call", new { name = "get_event_context", arguments = new { sessionId = "stdio-session", eventId, before = 0, after = 0 } }).ConfigureAwait(true);
        Assert.Equal(eventId, context.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("eventId").GetString());
        var arguments = new
        {
            packetId,
            idempotencyKey = "stdio-review-key",
            findings = new[]
            {
                new
                {
                    statement = "Use synthetic fixtures for stdio tests.", category = "preference", scope = "project", project = "synthetic-project", basis = "inferred", rationale = "The session uses a synthetic fixture.",
                    evidence = new[] { new { sessionId = "stdio-session", eventId, quote = "Synthetic stdio fixture" } }
                }
            }
        };
        var firstSubmission = first.RequestAsync("tools/call", new { name = "submit_review", arguments });
        var secondSubmission = second.RequestAsync("tools/call", new { name = "submit_review", arguments });
        using var submitted = await firstSubmission.ConfigureAwait(true);
        using var replayed = await secondSubmission.ConfigureAwait(true);
        var firstReceipt = submitted.RootElement.GetProperty("result").GetProperty("structuredContent");
        var secondReceipt = replayed.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Single(firstReceipt.GetProperty("insightIds").EnumerateArray());
        Assert.Equal(firstReceipt.GetProperty("insightIds")[0].GetString(), secondReceipt.GetProperty("insightIds")[0].GetString());
        Assert.NotEqual(firstReceipt.GetProperty("replayed").GetBoolean(), secondReceipt.GetProperty("replayed").GetBoolean());
        using var searched = await second.RequestAsync("tools/call", new { name = "search_insights", arguments = new { query = "synthetic", project = "synthetic-project" } }).ConfigureAwait(true);
        Assert.Empty(searched.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("items").EnumerateArray());
        // Apply the user's approval through the same service as the UI, with no web host running.
        using var services = new ServiceCollection().AddLogging().AddTurnglass(dataDirectory).BuildServiceProvider();
        var knowledge = services.GetRequiredService<IKnowledgeService>();
        var insightId = firstReceipt.GetProperty("insightIds")[0].GetString()!;
        var approved = await knowledge.ReviewInsightsAsync(new InsightReviewRequest([new(insightId, 1, "approve")]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("approved", Assert.Single(approved).Status);
        using var retrieved = await first.RequestAsync("tools/call", new { name = "search_insights", arguments = new { query = "synthetic", project = "synthetic-project" } }).ConfigureAwait(true);
        var item = Assert.Single(retrieved.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("items").EnumerateArray());
        Assert.Equal(insightId, item.GetProperty("id").GetString());
        Assert.Equal("approved", item.GetProperty("status").GetString());
    }

    private sealed class McpProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _diagnostics;
        private int _requestId;

        private McpProcess(Process process)
        {
            _process = process;
            _diagnostics = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        }

        internal static McpProcess Start(string dataDirectory, string codexHome)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add(FindMcpExecutable());
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(dataDirectory);
            start.Environment["CODEX_HOME"] = codexHome;
            start.Environment["TURNGLASS_COPILOT_AUTO_DISCOVER"] = "false";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            var process = Process.Start(start);
            Assert.NotNull(process);
            return new McpProcess(process);
        }

        internal async Task InitializeAsync()
        {
            using var result = await RequestAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "turnglass-tests", version = "1.0" } }).ConfigureAwait(true);
            Assert.Equal("turnglass", result.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            var instructions = result.RootElement.GetProperty("result").GetProperty("instructions").GetString();
            Assert.Contains("120000", instructions, StringComparison.Ordinal);
            Assert.Contains("untrusted", instructions, StringComparison.Ordinal);
            Assert.Contains("draft", instructions, StringComparison.Ordinal);
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }).AsMemory(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            await _process.StandardInput.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        internal async Task<JsonDocument> RequestAsync(string method, object parameters)
        {
            var id = ++_requestId;
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            await _process.StandardInput.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(true);
                Assert.True(line is not null, "The MCP process closed stdout: " + (_process.HasExited ? await _diagnostics.ConfigureAwait(true) : "Process is still running."));
                var response = JsonDocument.Parse(line);
                Assert.Equal("2.0", response.RootElement.GetProperty("jsonrpc").GetString());
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                {
                    Assert.False(response.RootElement.TryGetProperty("error", out _), line);
                    return response;
                }

                response.Dispose();
            }
        }

        internal async Task WaitForIndexAsync()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var result = await RequestAsync("tools/call", new { name = "get_index_status", arguments = new { } }).ConfigureAwait(true);
                var payload = result.RootElement.GetProperty("result");
                if (payload.TryGetProperty("structuredContent", out var status) && status.GetProperty("initialIndexComplete").GetBoolean())
                {
                    Assert.Equal(1, status.GetProperty("sessionCount").GetInt32());
                    return;
                }

                await Task.Delay(100, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            Assert.Fail("The synthetic index did not finish.");
        }

        internal async Task CloseAsync()
        {
            _process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(0, _process.ExitCode);
            var diagnostics = await _diagnostics.ConfigureAwait(true);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit();
            }

            _process.Dispose();
        }

        private static string FindMcpExecutable()
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = output.Parent?.Name ?? "Debug";
            for (var directory = output; directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "src", "Turnglass.Mcp", "bin", configuration, "net10.0", "Turnglass.Mcp.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("Build the MCP project before running its subprocess tests.");
        }
    }
}
