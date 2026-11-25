using System.Text.Json.Serialization;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata, NumberHandling = JsonNumberHandling.Strict, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DesktopRequest))]
[JsonSerializable(typeof(DesktopResponse))]
[JsonSerializable(typeof(DesktopProblem))]
[JsonSerializable(typeof(UsageReport))]
[JsonSerializable(typeof(PageResult<SessionView>), TypeInfoPropertyName = "SessionPage")]
[JsonSerializable(typeof(PageResult<EventView>), TypeInfoPropertyName = "EventPage")]
[JsonSerializable(typeof(SessionDetail))]
[JsonSerializable(typeof(LogChunk))]
[JsonSerializable(typeof(FilterOptions))]
[JsonSerializable(typeof(IndexStatus))]
[JsonSerializable(typeof(ViewerSettings))]
[JsonSerializable(typeof(InsightPage))]
[JsonSerializable(typeof(Insight))]
[JsonSerializable(typeof(IReadOnlyList<Insight>), TypeInfoPropertyName = "Insights")]
[JsonSerializable(typeof(InsightReviewRequest))]
[JsonSerializable(typeof(InsightExportRequest))]
[JsonSerializable(typeof(InsightExport))]
[JsonSerializable(typeof(ReviewProgress))]
[JsonSerializable(typeof(ReviewPrompt))]
[JsonSerializable(typeof(EventContext))]
[JsonSerializable(typeof(CopilotUsageReport))]
[JsonSerializable(typeof(CopilotIndexStatus))]
[JsonSerializable(typeof(CopilotFilterOptions))]
[JsonSerializable(typeof(CopilotSettings))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;

internal sealed record DesktopRequest(int Version, string Id, string Method, string Path, string? Body);

internal sealed record DesktopResponse(int Version, string Id, int Status, string? Body);

internal sealed record DesktopProblem(string Type, string Title, int Status, string Detail);
