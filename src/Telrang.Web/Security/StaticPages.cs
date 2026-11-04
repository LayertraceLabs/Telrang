using System.Net;

namespace Telrang.Web.Security;

/// <summary>
/// Minimal, script-free error pages written straight to the response (429, form expired, blocked).
/// No nonce and no Blazor: they are served by middleware and rate limiters before any page runs.
/// Writing a body also keeps UseStatusCodePagesWithReExecute from replacing them with "Not found".
/// </summary>
public static class StaticPages
{
    public static async Task WriteAsync(HttpContext http, int status, string title, string line, TimeSpan? retryAfter = null, string? back = null)
    {
        if (http.Response.HasStarted) return;
        http.Response.Clear();
        http.Response.StatusCode = status;
        if (retryAfter is { } r) http.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(1, r.TotalSeconds))).ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentType = "text/html; charset=utf-8";
        var link = back is null
            ? "<a href=\"/\">Home</a>"
            : $"<a href=\"{WebUtility.HtmlEncode(back)}\">Back</a> · <a href=\"/\">Home</a>";
        var theme = http.RequestServices.GetService<Features.Branding.ThemeService>();
        var scheme = theme?.BootstrapThemeFor(http) is { } t ? $" data-bs-theme=\"{t}\"" : ""; // visitor's mode, else the default
        // The variant without custom CSS: these pages must stay readable whatever the custom CSS does.
        var themeLink = theme is null ? "" : $"<link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(theme.AdminUrl)}\" />";
        // Same icon links as App.razor: an uploaded favicon, else the static PNG plus the palette's SVG.
        var uploaded = http.RequestServices.GetService<Features.Branding.BrandingService>()?.Url(Features.Branding.BrandingService.Favicon);
        var icon = uploaded is not null ? $"<link rel=\"icon\" type=\"image/png\" href=\"{WebUtility.HtmlEncode(uploaded)}\" />"
            : theme is null ? "<link rel=\"icon\" type=\"image/png\" href=\"/favicon.png\" sizes=\"64x64\" />"
            : $"<link rel=\"icon\" type=\"image/png\" href=\"/favicon.png\" sizes=\"64x64\" /><link rel=\"icon\" type=\"image/svg+xml\" href=\"{WebUtility.HtmlEncode(theme.FaviconUrl)}\" />";
        // Same slim header as PublicLayout (name only: no account menu without Blazor).
        var name = http.RequestServices.GetService<Features.Settings.SettingsService>()?.AppName ?? "Telrang";
        await http.Response.WriteAsync($"""
            <!DOCTYPE html>
            <html lang="en"{scheme}>
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>{WebUtility.HtmlEncode(title)}</title>
            <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css" />
            <link rel="stylesheet" href="/app.css" />
            {themeLink}
            {icon}
            </head>
            <body>
            <div class="shell shell-public">
            <header class="app-header app-header-slim"><div class="shell-container app-header-inner"><a class="app-brand" href="/"><svg class="brand-mark" aria-hidden="true" focusable="false"><use href="/icons.svg#brand-mark"></use></svg><span class="app-brand-name">{WebUtility.HtmlEncode(name)}</span></a></div></header>
            <main class="app-main"><article class="content shell-container">
            <h1>{WebUtility.HtmlEncode(title)}</h1>
            <p>{WebUtility.HtmlEncode(line)}</p>
            <p>{link}</p>
            </article></main>
            </div>
            </body>
            </html>
            """, http.RequestAborted);
    }

    /// <summary>"N minutes" (at least 1) for messages.</summary>
    public static string Minutes(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    public static Task TooManyAsync(HttpContext http, string line, TimeSpan? retryAfter = null) =>
        WriteAsync(http, StatusCodes.Status429TooManyRequests, "Too many requests", line, retryAfter);

    /// <summary>Persistent CIDR block (DESIGN.md §17): 403, one line.</summary>
    public static Task BlockedAsync(HttpContext http) =>
        WriteAsync(http, StatusCodes.Status403Forbidden, "Blocked", "Access from your network is blocked.");

    /// <summary>A form body over the endpoint's size limit (413) or with too many or too long values (400).</summary>
    public static Task FormTooLargeAsync(HttpContext http, int status) =>
        WriteAsync(http, status, "Form too large", "Reload the page and send the form again.", back: SameSiteReferer(http));

    public static Task FormExpiredAsync(HttpContext http, string? line = null) =>
        WriteAsync(http, StatusCodes.Status400BadRequest, "Form expired", line ?? "Reload the page and submit the form again.",
            back: SameSiteReferer(http));

    /// <summary>The Referer path when it points at this site (a "Back" link to the form), else null.</summary>
    private static string? SameSiteReferer(HttpContext http)
    {
        if (!Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)) return null;
        return string.Equals(referer.Host, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase) ? referer.PathAndQuery : null;
    }
}
