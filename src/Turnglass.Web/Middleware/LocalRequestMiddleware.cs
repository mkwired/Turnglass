using System.Net;

namespace Turnglass.Web.Middleware;

public sealed class LocalRequestMiddleware(RequestDelegate next, IHostEnvironment environment, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLoopbackHost(context.Request.Host.Host) || (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if ((!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(origin, context.Request, environment.IsDevelopment(), configuration["Turnglass:FrontendOrigin"])) ||
            string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'";
        await next(context).ConfigureAwait(false);
    }

    public static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }

    private static bool IsAllowedOrigin(string origin, HttpRequest request, bool development, string? frontendOrigin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host))
        {
            return false;
        }

        var sameOrigin = string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
        return sameOrigin || (development && uri.Scheme == "http" &&
            (uri.Port == 5173 || string.Equals(origin.TrimEnd('/'), frontendOrigin?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
    }
}
