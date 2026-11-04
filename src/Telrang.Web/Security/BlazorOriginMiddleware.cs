using Telrang.Web.Features.Settings;

namespace Telrang.Web.Security;

/// <summary>
/// Refuses Blazor circuit requests (<c>/_blazor</c>: negotiate and the WebSocket upgrade) whose <c>Origin</c> header is
/// present and is not this site (pentest R2 Info a): WebSockets are not covered by CORS, so another site could open a
/// circuit with the visitor's cookies. This site is the Public URL's origin when set, and the request's own host; a
/// loopback origin is accepted on a loopback host (development). Requests without Origin (non-browser clients) pass.
/// </summary>
public sealed class BlazorOriginMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http, SettingsService settings)
    {
        if (http.Request.Path.StartsWithSegments("/_blazor")
            && http.Request.Headers.Origin is { Count: > 0 } origin
            && !IsAllowed(origin.ToString(), http.Request, settings.Current.PublicUrl))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(http);
    }

    public static bool IsAllowed(string origin, HttpRequest request, string? publicUrl)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || o.Scheme is not ("https" or "http")) return false; // also "null"
        if (publicUrl is not null && Uri.TryCreate(publicUrl, UriKind.Absolute, out var p)
            && o.Scheme == p.Scheme && string.Equals(o.Authority, p.Authority, StringComparison.OrdinalIgnoreCase))
            return true;
        // The request's own host (scheme not compared: a proxy may not pass X-Forwarded-Proto). A browser cannot set Host
        // to another site's name for a cross-site request.
        if (string.Equals(o.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)) return true;
        return SettingsService.IsLoopbackHost(o.Host) && SettingsService.IsLoopbackHost(request.Host.Host);
    }
}
