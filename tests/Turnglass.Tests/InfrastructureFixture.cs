using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

internal sealed class InfrastructureFixture : IDisposable
{
    internal static DateTimeOffset Now { get; } = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Turnglass.Tests", Guid.NewGuid().ToString("N"));
    internal string Sources { get; }
    internal string Archive { get; }
    internal string Data { get; }
    internal ServiceProvider Services { get; }
    internal IViewerService Viewer => Services.GetRequiredService<IViewerService>();
    internal SessionIndexer Indexer => Services.GetRequiredService<SessionIndexer>();

    internal InfrastructureFixture()
    {
        Sources = Path.Combine(Root, "codex", "sessions");
        Archive = Path.Combine(Root, "codex", "archived_sessions");
        Data = Path.Combine(Root, "data");
        Directory.CreateDirectory(Sources);
        Directory.CreateDirectory(Archive);
        Services = CreateServices();
    }

    internal ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixtureClock());
        services.AddTurnglass(new ViewerOptions { DataDirectory = Data, Sources = [Sources, Archive] });
        return services.BuildServiceProvider();
    }

    internal async Task<string> WriteAsync(string name, params object[] records)
    {
        var path = Path.Combine(Sources, name);
        await File.WriteAllTextAsync(path, string.Join('\n', records.Select(Serialize)) + "\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        return path;
    }

    internal static string Serialize(object record) => record is string raw ? raw : JsonSerializer.Serialize(record);

    internal static object Record(string type, object payload, long ordinal = 1, DateTimeOffset? timestamp = null) => new { type, payload, ordinal, timestamp = timestamp ?? Now.AddHours(-1) };

    internal static object Meta(string id, string? parent = null, long? boundary = null, string? title = null) => Record("session_meta", new { id, cwd = "D:\\project", parent_thread_id = parent, subagent_history_start_ordinal = boundary, title });

    internal static object Context(string model = "gpt-6-astra", long ordinal = 2, string turn = "turn-1") => Record("turn_context", new { model, turn_id = turn }, ordinal);

    internal static object Settings(string id, string tier = "default", long ordinal = 3) => Record("event_msg", new { type = "thread_settings_applied", thread_id = id, thread_settings = new { model = "gpt-6-astra", service_tier = tier } }, ordinal);

    internal static object Tokens(long input, long output = 10, long cached = 20, long reasoning = 2) => new { input_tokens = input, cached_input_tokens = cached, output_tokens = output, reasoning_output_tokens = reasoning, cache_write_input_tokens = 0, total_tokens = input + output };

    internal static object Usage(string id, string response = "response-1", long input = 100, long output = 10, long ordinal = 4, object? cumulative = null, string turn = "turn-1") => Record("token_usage_record", new { thread_id = id, response_id = response, turn_id = turn, usage = Tokens(input, output, Math.Min(20, input), Math.Min(2, output)), thread_token_usage = cumulative }, ordinal);

    internal static object Legacy(object total, object? last = null, long ordinal = 5) => Record("event_msg", new { type = "token_count", info = new { total_token_usage = total, last_token_usage = last ?? total } }, ordinal);

    internal static object Message(string text, string role = "user", long ordinal = 6) => Record("response_item", new { type = "message", role, content = new[] { new { type = "input_text", text } } }, ordinal);

    public void Dispose()
    {
        Services.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
