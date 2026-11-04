using System.Security.Cryptography;

namespace Telrang.Web.Security;

/// <summary>
/// Adds CSP and the other security headers to every response. CSP uses a per-request nonce for
/// the one inline script Blazor needs (the import map, see App.razor). Everything else is
/// served from 'self'.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string NonceKey = "Telrang.CspNonce";

    public static string GetNonce(HttpContext context) =>
        context.Items.TryGetValue(NonceKey, out var value) && value is string nonce
            ? nonce
            : throw new InvalidOperationException("SecurityHeadersMiddleware is not in the pipeline.");

    public Task InvokeAsync(HttpContext context)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)); // hex: no HTML-escaping surprises
        context.Items[NonceKey] = nonce;

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            var captcha = context.RequestServices.GetService<Features.Settings.SettingsService>()?.Current.Captcha;
            var extra = captcha is { Enabled: true } ? CaptchaProviders.Origins(captcha.Provider) : [];
            headers.ContentSecurityPolicy = BuildCsp(context.Request, nonce, extra);
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY"; // legacy twin of frame-ancestors 'none'
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            return Task.CompletedTask;
        });

        return next(context);
    }

    /// <param name="extraOrigins">Captcha provider origins, only while a captcha is enabled (DESIGN.md §8).</param>
    internal static string BuildCsp(HttpRequest request, string nonce, IReadOnlyCollection<string> extraOrigins)
    {
        var extra = extraOrigins.Count == 0 ? "" : " " + string.Join(' ', extraOrigins);
        // Blazor Server talks over a WebSocket to the same host. CSP3 'self' covers ws(s) on the
        // same origin, but older Safari versions do not, so name the exact origin explicitly.
        var wsOrigin = (request.IsHttps ? "wss://" : "ws://") + request.Host.Value;

        var csp = "default-src 'self'; " +
                  $"script-src 'self' 'nonce-{nonce}'{extra}; " +
                  $"style-src 'self'{extra}; " +
                  "img-src 'self' data:; " +
                  "font-src 'self'; " +
                  $"connect-src 'self' {wsOrigin}{extra}; " +
                  $"frame-src 'self'{extra}; " +
                  "object-src 'none'; " +
                  "base-uri 'self'; " +
                  "form-action 'self'; " +
                  "frame-ancestors 'none'";

        if (request.IsHttps)
        {
            csp += "; upgrade-insecure-requests";
        }

        return csp;
    }
}

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();

    /// <summary>The CSP nonce for the current request, for inline scripts that must exist.</summary>
    public static string GetCspNonce(this HttpContext context) => SecurityHeadersMiddleware.GetNonce(context);
}
