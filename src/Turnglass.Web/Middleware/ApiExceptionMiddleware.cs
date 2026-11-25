using Turnglass.Core;

namespace Turnglass.Web.Middleware;

public sealed partial class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request").ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (TimeZoneNotFoundException)
        {
            await Results.Problem("Select a valid timezone.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid timezone").ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (KnowledgeConflictException exception)
        {
            await Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict, title: "Review conflict").ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Results.Problem("The requested record was not found.", statusCode: StatusCodes.Status404NotFound, title: "Not found").ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogIndexUnavailable(logger, exception);
            context.Response.Headers.RetryAfter = "1";
            await Results.Problem("The local index is unavailable or initializing. Check indexing diagnostics and retry.", statusCode: StatusCodes.Status503ServiceUnavailable, title: "Index unavailable").ExecuteAsync(context).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The viewer index is temporarily unavailable")]
    private static partial void LogIndexUnavailable(ILogger logger, Exception exception);
}
