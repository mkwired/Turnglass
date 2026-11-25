using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

/// <summary>Dispatches the desktop bridge's fixed application API without an HTTP server.</summary>
public sealed class DesktopApiDispatcher(IViewerService viewer, IKnowledgeService knowledge, ICopilotUsageService copilot)
{
    private const int MaximumRequestCharacters = 1_048_576;

    public async Task<string> InvokeAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = string.Empty;
        DesktopResult result;
        try
        {
            if (string.IsNullOrEmpty(requestJson) || requestJson.Length > MaximumRequestCharacters)
            {
                throw new DesktopRequestException("The request envelope is empty or too large.");
            }

            using var document = JsonDocument.Parse(requestJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DesktopRequestException("The request envelope must be a JSON object.");
            }

            if (document.RootElement.TryGetProperty("id", out var requestId) && requestId.ValueKind == JsonValueKind.String)
            {
                var candidate = requestId.GetString();
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 128 && !candidate.Any(char.IsControl))
                {
                    id = candidate;
                }
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.EnumerateObject().Any(property => !names.Add(property.Name)))
            {
                throw new DesktopRequestException("The request envelope contains duplicate fields.");
            }

            var request = JsonSerializer.Deserialize(requestJson, DesktopJsonContext.Default.DesktopRequest);
            if (request is null || request.Version != 1 || id.Length == 0 || request.Method is not ("GET" or "PUT" or "POST"))
            {
                throw new DesktopRequestException("Use bridge version 1, a valid request ID, and GET, PUT, or POST.");
            }

            var route = DesktopRoute.Parse(request.Path);
            result = await DispatchAsync(request, route, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DesktopRequestException exception)
        {
            result = Problem(400, "Invalid request", exception.Message);
        }
        catch (JsonException)
        {
            result = Problem(400, "Invalid request", "The request contains invalid JSON or does not match the expected fields and types.");
        }
        catch (ArgumentException exception)
        {
            result = Problem(400, "Invalid request", exception.Message);
        }
        catch (TimeZoneNotFoundException)
        {
            result = Problem(400, "Invalid timezone", "Select a valid timezone.");
        }
        catch (KnowledgeConflictException exception)
        {
            result = Problem(409, "Review conflict", exception.Message);
        }
        catch (KeyNotFoundException)
        {
            result = NotFound();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or SqliteException)
        {
            result = Problem(503, "Index unavailable", "The local index is unavailable or initializing. Check indexing diagnostics and retry.");
        }
        catch (Exception)
        {
            result = Problem(500, "Request failed", "The request could not be completed. Please retry.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return JsonSerializer.Serialize(new DesktopResponse(1, id, result.Status, result.Body), DesktopJsonContext.Default.DesktopResponse);
    }

    private async Task<DesktopResult> DispatchAsync(DesktopRequest request, DesktopRoute route, CancellationToken cancellationToken)
    {
        var query = route.Query;
        switch (route.Segments)
        {
            case ["usage"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.GetUsageAsync(new UsageQuery(query.Text("mode") ?? "calendar", query.Text("timezone"), query.Text("project"), query.Text("model"), query.Date("from"), query.Date("to")), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.UsageReport);
            case ["sessions"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.ListSessionsAsync(new SessionFilter(query.Text("query"), query.Text("project"), query.Text("model"), query.Date("from"), query.Date("to"), query.Text("cursor"), query.Integer("limit", 30)), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.SessionPage);
            case ["sessions", var sessionId]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.SessionDetail);
            case ["sessions", var sessionId, "events"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.ReadEventsAsync(sessionId, query.Text("cursor"), query.Integer("limit", 50), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.EventPage);
            case ["sessions", var sessionId, "records", var eventId]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.ReadLogRecordAsync(sessionId, eventId, query.Integer("offset", 0), query.Integer("limit", 16000), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.LogChunk);
            case ["search"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                var search = query.Text("query");
                if (string.IsNullOrWhiteSpace(search))
                {
                    throw new DesktopRequestException("The query field is required.");
                }

                return Ok(await viewer.SearchLogsAsync(search, query.Text("sessionId"), query.Text("cursor"), query.Integer("limit", 30), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.EventPage);
            case ["filters"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.GetFiltersAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.FilterOptions);
            case ["index"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.IndexStatus);
            case ["settings"]:
                if (request.Method == "PUT")
                {
                    return Ok(await viewer.UpdateSettingsAsync(Body(request, DesktopJsonContext.Default.ViewerSettings), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.ViewerSettings);
                }

                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await viewer.GetSettingsAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.ViewerSettings);
            case ["index", "rebuild"]:
                if (!IsMethod(request, "POST")) { return MethodNotAllowed(); }
                await viewer.RebuildAsync(cancellationToken).ConfigureAwait(false);
                return Ok(await viewer.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.IndexStatus);
            case ["insights"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await knowledge.ListInsightsAsync(new InsightQuery(query.Text("query"), query.Text("project"), query.Text("category"), query.Text("status"), query.Text("cursor"), query.Integer("limit", 30)), cancellationToken: cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.InsightPage);
            case ["insights", "review"] when request.Method == "POST":
                return Ok(await knowledge.ReviewInsightsAsync(Body(request, DesktopJsonContext.Default.InsightReviewRequest), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.Insights);
            case ["insights", "export"] when request.Method == "POST":
                return Ok(await knowledge.ExportInsightsAsync(Body(request, DesktopJsonContext.Default.InsightExportRequest), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.InsightExport);
            case ["insights", var insightId]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await knowledge.GetInsightAsync(insightId, cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.Insight);
            case ["reviews", "progress"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await knowledge.GetReviewProgressAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.ReviewProgress);
            case ["reviews", "prompt"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(knowledge.GetReviewPrompt(query.Text("project")), DesktopJsonContext.Default.ReviewPrompt);
            case ["sessions", var sessionId, "events", var eventId, "context"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await knowledge.GetEventContextAsync(sessionId, eventId, query.Integer("before", 3), query.Integer("after", 3), query.Integer("maxCharacters", 16000), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.EventContext);
            case ["copilot", "usage"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await copilot.GetUsageAsync(new CopilotUsageQuery(query.Text("mode") ?? "calendar", query.Text("timezone"), query.Text("client"), query.Text("model"), query.Date("from"), query.Date("to")), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.CopilotUsageReport);
            case ["copilot", "index"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await copilot.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.CopilotIndexStatus);
            case ["copilot", "filters"]:
                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await copilot.GetFiltersAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.CopilotFilterOptions);
            case ["copilot", "settings"]:
                if (request.Method == "PUT")
                {
                    return Ok(await copilot.UpdateSettingsAsync(Body(request, DesktopJsonContext.Default.CopilotSettings), cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.CopilotSettings);
                }

                if (!IsMethod(request, "GET")) { return MethodNotAllowed(); }
                return Ok(await copilot.GetSettingsAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.CopilotSettings);
            case ["copilot", "index", "rescan"]:
                if (!IsMethod(request, "POST")) { return MethodNotAllowed(); }
                await copilot.RescanAsync(cancellationToken).ConfigureAwait(false);
                return Ok(await copilot.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false), DesktopJsonContext.Default.CopilotIndexStatus);
            default:
                return NotFound();
        }
    }

    private static bool IsMethod(DesktopRequest request, string method)
    {
        if (request.Method != method)
        {
            return false;
        }

        if (request.Body is not null)
        {
            throw new DesktopRequestException("This operation does not accept a request body.");
        }

        return true;
    }

    private static T Body<T>(DesktopRequest request, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new DesktopRequestException("A JSON request body is required.");
        }

        var value = JsonSerializer.Deserialize(request.Body, typeInfo) ?? throw new DesktopRequestException("A JSON request body is required.");
        return value;
    }

    private static DesktopResult Ok<T>(T? value, JsonTypeInfo<T> typeInfo) where T : class => value is null ? NotFound() : new(200, JsonSerializer.Serialize(value, typeInfo));

    private static DesktopResult NotFound() => Problem(404, "Not found", "The requested record was not found.");

    private static DesktopResult MethodNotAllowed() => Problem(405, "Method not allowed", "The requested method is not supported for this operation.");

    private static DesktopResult Problem(int status, string title, string detail) => new(status, JsonSerializer.Serialize(new DesktopProblem("about:blank", title, status, detail), DesktopJsonContext.Default.DesktopProblem));

    private sealed record DesktopResult(int Status, string? Body);
}

internal sealed class DesktopRequestException(string message) : Exception(message);

internal sealed record DesktopRoute(string[] Segments, DesktopQuery Query)
{
    internal static DesktopRoute Parse(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 16384 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('#') || path.Contains('\\') || path.Any(char.IsControl))
        {
            throw new DesktopRequestException("Use a relative application API path without fragments or traversal.");
        }

        var separator = path.IndexOf('?');
        var route = separator < 0 ? path : path[..separator];
        var segments = route[1..].Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = Decode(segments[index], false);
            if (segments[index].Length == 0 || segments[index].Contains('\\') || segments[index].Any(char.IsControl) || segments[index].Split('/').Any(part => part is "." or ".."))
            {
                throw new DesktopRequestException("Use a relative application API path without fragments or traversal.");
            }
        }

        return new(segments, DesktopQuery.Parse(separator < 0 ? string.Empty : path[(separator + 1)..]));
    }

    internal static string Decode(string value, bool query)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%')
            {
                if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                {
                    throw new DesktopRequestException("The request path or query contains invalid escaping.");
                }

                index += 2;
            }
        }

        return Uri.UnescapeDataString(query ? value.Replace('+', ' ') : value);
    }
}

internal sealed class DesktopQuery(Dictionary<string, string> values)
{
    internal static DesktopQuery Parse(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = DesktopRoute.Decode(separator < 0 ? pair : pair[..separator], true);
            var value = DesktopRoute.Decode(separator < 0 ? string.Empty : pair[(separator + 1)..], true);
            if (key.Length == 0 || key.Any(char.IsControl) || value.Any(char.IsControl) || !values.TryAdd(key, value))
            {
                throw new DesktopRequestException("The query contains an empty, duplicate, or invalid field.");
            }
        }

        return new(values);
    }

    internal string? Text(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    internal int Integer(string name, int fallback)
    {
        if (!values.TryGetValue(name, out var value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new DesktopRequestException($"The {name} field must be an integer.");
        }

        return number;
    }

    internal DateTimeOffset? Date(string name)
    {
        var value = Text(name);
        if (value is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            throw new DesktopRequestException($"The {name} field must be a valid date and time.");
        }

        return timestamp;
    }
}
