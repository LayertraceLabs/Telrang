using Telrang.Web.Features.Settings;

namespace Telrang.Web.Security;

/// <summary>
/// Refuses requests another site makes a visitor's browser send, before the abuse block, the rate limiter and the
/// form are touched (DESIGN.md §31): they answer 403, count against no limit and record no abuse signal, so a page
/// elsewhere cannot get its visitors limited or blocked here. A link from another site (a top-level GET navigation)
/// passes. Decided from the browser's Fetch Metadata headers; a browser without them is judged by <c>Origin</c> on
/// unsafe methods only (the same rule as <see cref="BlazorOriginMiddleware"/>). Clients that send neither (curl, link previews) pass: they carry no visitor's cookies.
/// Endpoints marked <see cref="AbuseExemptAttribute"/> (static assets, branding, health) are not checked.
/// </summary>
public sealed class CrossSiteRequestMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http, SettingsService settings)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<AbuseExemptAttribute>() is null
            && CrossSiteRequests.IsRefused(http.Request, settings.Current.PublicUrl))
            return StaticPages.WriteAsync(http, StatusCodes.Status403Forbidden, "Request refused", "Open the page on this site and try again.");
        return next(http);
    }
}

public static class CrossSiteRequests
{
    public static bool IsRefused(HttpRequest request, string? publicUrl)
    {
        var safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length == 0)
        {
            if (safe || !request.Headers.TryGetValue("Origin", out var origin)) return false;
            return !BlazorOriginMiddleware.IsAllowed(origin.ToString(), request, publicUrl); // this site, as for /_blazor
        }
        if (site is "same-origin" or "none") return false;
        // cross-site, same-site (a sibling host) or anything unknown: only a link the visitor follows.
        return !(safe
            && request.Headers["Sec-Fetch-Mode"].ToString() == "navigate"
            && request.Headers["Sec-Fetch-Dest"].ToString() is not ("iframe" or "frame" or "object" or "embed" or "fencedframe"));
    }
}
