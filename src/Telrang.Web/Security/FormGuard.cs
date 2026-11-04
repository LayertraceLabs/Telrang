using System.Globalization;
using System.Security.Cryptography;
using Telrang.Web.Features.Status;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Security;

public enum FormCheck { Ok, Honeypot, TooFast, Expired }

/// <summary>
/// Bot checks for publicly exposed forms (DESIGN.md §14.7): a honeypot field that must stay empty and
/// a Data-Protection signed render time that must be between 2 seconds and 2 hours old when the form
/// comes back. Rendered by <c>FormGuardFields</c>; checked by <see cref="FormGuardMiddleware"/> for
/// the guarded paths. The vote form carries the same fields, but BallotService checks them (with a 12 h maximum), because
/// the middleware's static page would lose the ballot (DESIGN.md §21.1).
/// </summary>
public sealed class FormGuard(IDataProtectionProvider dataProtection, TimeProvider time, IOptions<AbuseOptions> options)
{
    public const string HoneypotField = "website";
    public const string StampField = "_fts";

    private readonly IDataProtector protector = dataProtection.CreateProtector("Telrang.FormStamp.v1");

    public string CreateStamp() =>
        protector.Protect(time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    public FormCheck Check(string? honeypot, string? stamp) => Check(honeypot, stamp, null);

    /// <param name="maxAge">Oldest stamp accepted; null = <c>Abuse:FormMaxMinutes</c>.</param>
    public FormCheck Check(string? honeypot, string? stamp, TimeSpan? maxAge)
    {
        if (!string.IsNullOrEmpty(honeypot)) return FormCheck.Honeypot;
        if (string.IsNullOrEmpty(stamp) || stamp.Length > 1000) return FormCheck.Expired;
        long issuedMs;
        try
        {
            if (!long.TryParse(protector.Unprotect(stamp), NumberStyles.None, CultureInfo.InvariantCulture, out issuedMs)) return FormCheck.Expired;
        }
        catch (CryptographicException)
        {
            return FormCheck.Expired;
        }
        return CheckAge(DateTimeOffset.FromUnixTimeMilliseconds(issuedMs), maxAge);
    }

    /// <summary>The age rule on its own.</summary>
    public FormCheck CheckAge(DateTimeOffset issuedAt, TimeSpan? maxAge = null)
    {
        var age = time.GetUtcNow() - issuedAt;
        if (age < TimeSpan.FromSeconds(options.Value.FormMinSeconds)) return FormCheck.TooFast;
        if (age > (maxAge ?? TimeSpan.FromMinutes(options.Value.FormMaxMinutes))) return FormCheck.Expired;
        return FormCheck.Ok;
    }

    public static string Message(FormCheck check) => check switch
    {
        FormCheck.TooFast => "Submitted too fast. Wait a few seconds and submit again.",
        _ => "Reload the page and submit the form again.",
    };

    public static AbuseSignal Signal(FormCheck check) => check == FormCheck.Honeypot ? AbuseSignal.Honeypot : AbuseSignal.FormTimestamp;

    /// <summary>POST paths whose forms carry the guard fields.</summary>
    public static bool IsGuarded(PathString path)
    {
        var p = path.Value?.TrimEnd('/') ?? "";
        if (p.Equals("/Account/Register", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/Account/ForgotPassword", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/Account/ResendEmailConfirmation", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/setup", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/setup/recover", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/setup/account/", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}

/// <summary>
/// Refuses guarded form posts whose honeypot is filled or whose signed stamp is missing, too fresh or
/// too old (400 page "Form expired"), and records an abuse signal. Runs after antiforgery.
/// </summary>
public sealed class FormGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, FormGuard guard, AbuseTracker abuse, UserManager<Data.ApplicationUser> users)
    {
        if (HttpMethods.IsPost(http.Request.Method) && FormGuard.IsGuarded(http.Request.Path) && http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var check = guard.Check(form[FormGuard.HoneypotField], form[FormGuard.StampField]);
            if (check != FormCheck.Ok)
            {
                abuse.Record(ClientKeys.ForAbuse(http), users.GetUserId(http.User), FormGuard.Signal(check));
                await StaticPages.FormExpiredAsync(http, FormGuard.Message(check));
                return;
            }
        }
        await next(http);
    }
}

/// <summary>Endpoint metadata: <see cref="AbuseBlockMiddleware"/> lets the request through even for a blocked client.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AbuseExemptAttribute : Attribute
{
    public static readonly AbuseExemptAttribute Instance = new();
}

public static class AbuseExemptExtensions
{
    /// <summary>Static content that a block page needs (stylesheets, images) or probes (health): never blocked.</summary>
    public static TBuilder ExemptFromAbuseBlock<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AbuseExemptAttribute.Instance);
}

/// <summary>
/// Turns a failed antiforgery check (e.g. a form restored after a browser restart: the antiforgery
/// cookie is a session cookie) into the static "Form expired" page instead of a raw 400.
/// </summary>
public sealed class AntiforgeryFailureMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>() is { IsValid: false })
            return StaticPages.FormExpiredAsync(http);
        return next(http);
    }
}

/// <summary>
/// Runs after forwarded headers, security headers and routing, before the rate limiter and authentication (Program.cs):
/// counts requests and 429s for the Status tab, and answers requests from blocked clients or accounts. Persistent CIDR blocks (<see cref="BlockedRangeService"/>,
/// DESIGN.md §17) come first and get the 403 "blocked" page; then the in-memory tracker (429). Both use the
/// connection address after the trusted forwarded headers. Signed-in admins are never blocked (they hold
/// the keys to clear blocks). Endpoints marked <see cref="AbuseExemptAttribute"/> pass (static assets, branding, health),
/// so the block page has its stylesheet; routing has run, so this is endpoint metadata, not a path suffix (pentest AV-15a).
/// </summary>
public sealed class AbuseBlockMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, AbuseTracker abuse, BlockedRangeService ranges, AppMetrics metrics, UserManager<Data.ApplicationUser> users,
        TimeProvider time)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>() is not null)
        {
            await next(http); // status page re-execution: already counted and checked
            return;
        }
        metrics.Requests.Add(1);
        if (http.GetEndpoint()?.Metadata.GetMetadata<AbuseExemptAttribute>() is null)
        {
            // UseAuthentication runs after this middleware; read the cookie here, only when it matters.
            var user = http.User;
            if (user.Identity?.IsAuthenticated != true)
            {
                var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                if (auth.Succeeded) user = auth.Principal;
            }
            var admin = user.IsInRole(AppRoles.Admin);
            if (!admin && ranges.IsBlocked(http.Connection.RemoteIpAddress))
            {
                await StaticPages.BlockedAsync(http);
                return;
            }
            var block = admin ? null : abuse.Find(http.Connection.RemoteIpAddress, users.GetUserId(user));
            if (block is not null)
            {
                var left = block.Until - time.GetUtcNow();
                metrics.TooManyRequests.Add(1);
                await StaticPages.TooManyAsync(http, $"Try again in {StaticPages.Minutes(left)}.", left);
                return;
            }
        }
        await next(http);
        if (http.Response.StatusCode == StatusCodes.Status429TooManyRequests) metrics.TooManyRequests.Add(1);
    }
}
