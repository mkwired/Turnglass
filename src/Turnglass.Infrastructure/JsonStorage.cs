using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

internal static class JsonStorage
{
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());

    internal static T Deserialize<T>(string value)
    {
        var result = JsonSerializer.Deserialize(value, TypeInfo<T>());
        if (result is not null)
        {
            return result;
        }

        throw new InvalidDataException("Stored data is empty.");
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>)(InfrastructureJsonContext.Default.GetTypeInfo(typeof(T))
            ?? TurnglassJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"Stored JSON type '{typeof(T)}' has no generated contract."));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ParserState))]
[JsonSerializable(typeof(ViewerService.PageCursor))]
[JsonSerializable(typeof(ViewerService.SessionCursorScope))]
[JsonSerializable(typeof(ViewerService.EventCursorScope))]
[JsonSerializable(typeof(ViewerService.SearchCursorScope))]
[JsonSerializable(typeof(KnowledgeService.Checkpoint))]
[JsonSerializable(typeof(KnowledgeService.SavedPacket))]
[JsonSerializable(typeof(KnowledgeService.SavedSubmission))]
[JsonSerializable(typeof(KnowledgeService.InsightCursor))]
[JsonSerializable(typeof(KnowledgeService.ReviewCursorScope))]
[JsonSerializable(typeof(KnowledgeService.InsightCursorScope))]
[JsonSerializable(typeof(CopilotParsedUsage))]
[JsonSerializable(typeof(CopilotIndexer.CopilotFileCheckpoint))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext;
