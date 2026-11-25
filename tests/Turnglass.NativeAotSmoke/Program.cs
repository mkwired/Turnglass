using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.NativeAotSmoke;

internal static class Program
{
    private static async Task<int> Main()
    {
        var root = Directory.CreateTempSubdirectory("Turnglass.NativeAotSmoke-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            Require(!RuntimeFeature.IsDynamicCodeSupported, "This check must run the published NativeAOT executable.");
            Require(!JsonSerializer.IsReflectionEnabledByDefault, "Reflection-based JSON must be disabled.");
            Require(TimeZoneInfo.FindSystemTimeZoneById("America/New_York").Id.Length > 0, "IANA timezones must work on Windows.");
            await ExerciseNewDataAsync(root.FullName, timeout.Token).ConfigureAwait(false);
            await ExerciseLegacyDataAsync(root.FullName, timeout.Token).ConfigureAwait(false);
            Console.WriteLine("NativeAOT smoke passed: dynamic code and JSON reflection disabled; SQLite FTS5/JSON, desktop dispatch, indexing, settings, insights, Copilot retention, and historical data verified.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // This path is the unique directory created above; no caller-supplied or user data path is accepted.
            root.Delete(recursive: true);
        }
    }

    private static async Task ExerciseNewDataAsync(string root, CancellationToken cancellationToken)
    {
        var sessions = Path.Combine(root, "synthetic-sessions");
        var copilotSources = Path.Combine(root, "synthetic-copilot");
        var copilotSession = Path.Combine(copilotSources, "native-copilot");
        var data = Path.Combine(root, "new-data");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(copilotSession);
        var rollout = Path.Combine(sessions, "synthetic.jsonl");
        await File.WriteAllTextAsync(rollout, """
            {"type":"session_meta","timestamp":"2026-11-24T12:00:00Z","payload":{"id":"native-session","cwd":"native-project","title":"Native AOT synthetic session"}}
            {"type":"turn_context","timestamp":"2026-11-24T12:00:00Z","payload":{"model":"native-model","turn_id":"turn-one"}}
            {"type":"event_msg","timestamp":"2026-11-24T12:00:00Z","payload":{"type":"thread_settings_applied","thread_id":"native-session","thread_settings":{"model":"native-model","service_tier":"default"}}}
            {"type":"token_usage_record","timestamp":"2026-11-24T12:00:00Z","payload":{"thread_id":"native-session","response_id":"response-one","turn_id":"turn-one","usage":{"input_tokens":120,"cached_input_tokens":20,"cache_write_input_tokens":0,"output_tokens":20,"reasoning_output_tokens":5}}}
            {"type":"response_item","timestamp":"2026-11-24T12:00:00Z","payload":{"type":"message","id":"message-one","role":"user","content":[{"type":"input_text","text":"Keep NativeAOT smoke checks focused."}]}}
            """ + "\n", cancellationToken).ConfigureAwait(false);
        var copilotLog = Path.Combine(copilotSession, "events.jsonl");
        await File.WriteAllTextAsync(copilotLog, """
            {"type":"session.usage_record","timestamp":"2026-11-24T12:00:00Z","data":{"usage":{"model":"native-model","inputTokens":300,"outputTokens":30,"cacheReadTokens":50,"cacheWriteTokens":0,"reasoningTokens":5,"accounting":{"sourceSessionId":"native-copilot","usageId":"native-call","sequence":1}}}}
            """ + "\n", cancellationToken).ConfigureAwait(false);

        var options = new ViewerOptions { DataDirectory = data, Sources = [sessions], CopilotSources = [new CopilotSourceRoot("visual-studio", copilotSources)] };
        string insightId;
        string eventId;
        ReviewSubmission submission;
        using (var services = CreateServices(options))
        {
            var viewer = services.GetRequiredService<IViewerService>();
            var knowledge = services.GetRequiredService<IKnowledgeService>();
            var dispatcher = CreateDispatcher(services);
            var settings = await viewer.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            var pricing = new PricingCatalog("native-smoke", "https://example.test/pricing",
                [new PriceRate("native-model", "standard", 0, null, 3m, 0.3m, 15m)], new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
            settings = settings with { Timezone = "America/New_York", Pricing = pricing };
            await SendAsync(dispatcher, "PUT", "/settings", JsonSerializer.Serialize(settings, TurnglassJsonContext.Default.ViewerSettings), cancellationToken).ConfigureAwait(false);
            await services.GetRequiredService<SessionIndexer>().RefreshAsync(cancellationToken).ConfigureAwait(false);
            await SendAsync(dispatcher, "POST", "/copilot/index/rescan", null, cancellationToken).ConfigureAwait(false);
            await VerifySqliteFeaturesAsync(data, cancellationToken).ConfigureAwait(false);

            var session = await SendAsync(dispatcher, "GET", "/sessions/native-session", null, cancellationToken).ConfigureAwait(false);
            Require(session.GetProperty("detail").GetProperty("ownUsage").GetProperty("tokens").GetProperty("totalTokens").GetInt64() == 140, "Codex usage was not indexed.");
            var search = await SendAsync(dispatcher, "GET", "/search?query=focused", null, cancellationToken).ConfigureAwait(false);
            Require(search.GetProperty("items").GetArrayLength() == 1, "FTS search must find the synthetic human message.");
            eventId = search.GetProperty("items")[0].GetProperty("id").GetString()!;
            await SendAsync(dispatcher, "GET", $"/sessions/native-session/records/{eventId}", null, cancellationToken).ConfigureAwait(false);
            await SendAsync(dispatcher, "GET", $"/sessions/native-session/events/{eventId}/context", null, cancellationToken).ConfigureAwait(false);

            var packet = await knowledge.GetReviewPacketAsync(new ReviewQuery(), cancellationToken).ConfigureAwait(false);
            Require(packet.PacketId is not null && packet.Session is not null, "A review packet must be available.");
            var draft = new InsightDraft("Keep NativeAOT smoke checks focused.", "preference", "project", packet.Session!.Project, null, "explicit", "The synthetic human message states this preference.",
                [new EvidenceInput(packet.Session.Id, eventId, "Keep NativeAOT smoke checks focused.")]);
            submission = new ReviewSubmission(packet.PacketId!, "native-review", [draft]);
            var receipt = await knowledge.SubmitReviewAsync(submission, cancellationToken).ConfigureAwait(false);
            insightId = receipt.InsightIds.Single();
            var review = new InsightReviewRequest([new InsightAction(insightId, 1, "approve")]);
            await SendAsync(dispatcher, "POST", "/insights/review", JsonSerializer.Serialize(review, TurnglassJsonContext.Default.InsightReviewRequest), cancellationToken).ConfigureAwait(false);
            var export = new InsightExportRequest([insightId], "json");
            var exported = await SendAsync(dispatcher, "POST", "/insights/export", JsonSerializer.Serialize(export, TurnglassJsonContext.Default.InsightExportRequest), cancellationToken).ConfigureAwait(false);
            using var exportedJson = JsonDocument.Parse(exported.GetProperty("content").GetString()!);
            Require(exportedJson.RootElement[0].GetProperty("status").GetString() == "approved", "Approved insight JSON export failed.");

            foreach (var path in new[] { "/usage", "/sessions", "/sessions/native-session/events", "/filters", "/index", "/insights", $"/insights/{insightId}", "/reviews/progress", "/reviews/prompt?project=native-project", "/copilot/usage", "/copilot/index", "/copilot/filters", "/copilot/settings" })
            {
                await SendAsync(dispatcher, "GET", path, null, cancellationToken).ConfigureAwait(false);
            }

            var copilot = await SendAsync(dispatcher, "GET", "/copilot/usage", null, cancellationToken).ConfigureAwait(false);
            Require(copilot.GetProperty("matchingRecordCount").GetInt32() == 1, "Copilot usage was not indexed.");
            var disabled = new CopilotSettings(false, []);
            await SendAsync(dispatcher, "PUT", "/copilot/settings", JsonSerializer.Serialize(disabled, TurnglassJsonContext.Default.CopilotSettings), cancellationToken).ConfigureAwait(false);
        }

        File.Delete(copilotLog);
        using (var services = CreateServices(options))
        {
            var dispatcher = CreateDispatcher(services);
            var settings = await SendAsync(dispatcher, "GET", "/settings", null, cancellationToken).ConfigureAwait(false);
            Require(settings.GetProperty("pricing").GetProperty("version").GetString() == "native-smoke", "Custom prices did not survive restart.");
            Require(settings.GetProperty("timezone").GetString() == "America/New_York", "Timezone did not survive restart.");
            var insight = await SendAsync(dispatcher, "GET", $"/insights/{insightId}", null, cancellationToken).ConfigureAwait(false);
            Require(insight.GetProperty("status").GetString() == "approved", "Approved insight did not survive restart.");
            var replay = await services.GetRequiredService<IKnowledgeService>().SubmitReviewAsync(submission, cancellationToken).ConfigureAwait(false);
            Require(replay.Replayed && replay.InsightIds.Single() == insightId, "Review idempotency did not survive restart.");
            await SendAsync(dispatcher, "POST", "/copilot/index/rescan", null, cancellationToken).ConfigureAwait(false);
            var retained = await SendAsync(dispatcher, "GET", "/copilot/usage", null, cancellationToken).ConfigureAwait(false);
            Require(retained.GetProperty("matchingRecordCount").GetInt32() == 1, "Rotated Copilot usage was not retained.");
            await SendAsync(dispatcher, "POST", "/index/rebuild", null, cancellationToken).ConfigureAwait(false);
            await services.GetRequiredService<SessionIndexer>().RefreshAsync(cancellationToken).ConfigureAwait(false);
            var rebuilt = await SendAsync(dispatcher, "GET", "/sessions/native-session", null, cancellationToken).ConfigureAwait(false);
            Require(rebuilt.GetProperty("detail").GetProperty("ownUsage").GetProperty("tokens").GetProperty("totalTokens").GetInt64() == 140, "Rebuilding changed indexed usage.");
        }
    }

    private static async Task ExerciseLegacyDataAsync(string root, CancellationToken cancellationToken)
    {
        var data = Path.Combine(root, "historical-data");
        Directory.CreateDirectory(data);
        foreach (var database in new[] { "session-viewer.db", "knowledge.db", "copilot-usage.db" })
        {
            using var resource = typeof(Program).Assembly.GetManifestResourceStream("Legacy." + database + ".gz")
                ?? throw new InvalidOperationException("Historical synthetic fixture is missing.");
            using var compressed = new GZipStream(resource, CompressionMode.Decompress);
            using var output = File.Create(Path.Combine(data, database));
            await compressed.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        using var services = CreateServices(new ViewerOptions { DataDirectory = data, Sources = [], CopilotSources = [] });
        var dispatcher = CreateDispatcher(services);
        var settings = await SendAsync(dispatcher, "GET", "/settings", null, cancellationToken).ConfigureAwait(false);
        Require(settings.GetProperty("pricing").GetProperty("version").GetString() == "Synthetic legacy custom prices", "Historical custom pricing was lost.");
        var session = await SendAsync(dispatcher, "GET", "/sessions/legacy-session", null, cancellationToken).ConfigureAwait(false);
        Require(session.GetProperty("detail").GetProperty("ownUsage").GetProperty("tokens").GetProperty("totalTokens").GetInt64() == 140, "Historical session JSON failed.");
        var insights = await SendAsync(dispatcher, "GET", "/insights?status=approved", null, cancellationToken).ConfigureAwait(false);
        Require(insights.GetProperty("items").GetArrayLength() == 1, "Historical approved insight was lost.");
        var progress = await SendAsync(dispatcher, "GET", "/reviews/progress", null, cancellationToken).ConfigureAwait(false);
        Require(progress.GetProperty("reviewedSessions").GetInt32() == 1, "Historical review checkpoint failed.");
        var copilot = await SendAsync(dispatcher, "GET", "/copilot/usage", null, cancellationToken).ConfigureAwait(false);
        Require(copilot.GetProperty("matchingRecordCount").GetInt32() == 1, "Historical retained Copilot JSON failed.");
        // No workers are started and no scan is requested: historical synthetic source paths are never accessed.
    }

    private static ServiceProvider CreateServices(ViewerOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new SmokeClock());
        services.AddTurnglass(options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static DesktopApiDispatcher CreateDispatcher(ServiceProvider services) => new(
        services.GetRequiredService<IViewerService>(), services.GetRequiredService<IKnowledgeService>(), services.GetRequiredService<ICopilotUsageService>());

    private static async Task<JsonElement> SendAsync(DesktopApiDispatcher dispatcher, string method, string path, string? body, CancellationToken cancellationToken)
    {
        var request = new SmokeRequest(1, "native-smoke", method, path, body);
        var envelope = await dispatcher.InvokeAsync(JsonSerializer.Serialize(request, SmokeJsonContext.Default.SmokeRequest), cancellationToken).ConfigureAwait(false);
        using var response = JsonDocument.Parse(envelope);
        Require(response.RootElement.GetProperty("status").GetInt32() == 200, $"{method} {path} failed: {envelope}");
        Require(response.RootElement.GetProperty("id").GetString() == request.Id, "Bridge correlation ID changed.");
        using var payload = JsonDocument.Parse(response.RootElement.GetProperty("body").GetString()!);
        return payload.RootElement.Clone();
    }

    private static async Task VerifySqliteFeaturesAsync(string data, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "session-viewer.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_extract('{\"value\":7}','$.value') + (SELECT count(*) FROM event_search WHERE event_search MATCH 'focused')";
        Require(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 8, "SQLite JSON or FTS5 native features are missing.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SmokeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);
    }
}

internal sealed record SmokeRequest(int Version, string Id, string Method, string Path, string? Body);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SmokeRequest))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
