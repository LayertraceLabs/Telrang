using Telrang.Web.Features.Branding;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Telrang.Web.Data;

namespace Telrang.Web.Features.Looks;

/// <summary>
/// <c>GET /p/{id}/look.css</c> (DESIGN.md §34.6): the poll's look as <c>--av-*</c> token overrides and nothing else, for
/// whoever may view the poll (<see cref="PollAccessService"/>; unknown or not viewable = 404, as the page; disabled = 410 for
/// everyone but the owner and admins, as the page). 404 too while
/// the admin setting is off and for a poll without a look. Long private cache when <c>?v=</c> is the current hash, else
/// <c>no-cache</c>. <c>GET /looks/preview.css?c=&lt;look code&gt;</c>: the look forms' preview sheet, for signed-in
/// accounts; the code goes through the look rules like any other, so a refused code is a 404.
/// </summary>
public static class LookEndpoints
{
    public const string Immutable = "private, max-age=31536000, immutable";

    public static IEndpointRouteBuilder MapLooks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/p/{id}/look.css", async (string id, HttpContext http, PollAccessService access, LookStyles styles,
                UserManager<ApplicationUser> users) =>
            {
                var a = await access.GetAsync(id, VisitorContext.ForUser(users.GetUserId(http.User)), http.RequestAborted);
                if (a is { Unavailable: true }) return Polls.PollUnavailable.Result(); // DESIGN.md §34.8
                if (!styles.Enabled || a is null || !a.CanView || styles.Shown(a.Poll) is not { } look) return Microsoft.AspNetCore.Http.Results.NotFound();
                var (css, version) = styles.PollCss(look);
                var headers = http.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.CacheControl = string.Equals(http.Request.Query["v"], version, StringComparison.Ordinal) ? Immutable : "no-cache";
                return Microsoft.AspNetCore.Http.Results.Text(css, "text/css; charset=utf-8");
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        endpoints.MapGet("/looks/preview.css", (HttpContext http, LookStyles styles) =>
            {
                if (ThemeLook.FromCode(http.Request.Query["c"]).Look is not { } look) return Microsoft.AspNetCore.Http.Results.NotFound();
                var headers = http.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.CacheControl = Immutable; // the content is the code in the URL
                return Microsoft.AspNetCore.Http.Results.Text(styles.PreviewCss(look), "text/css; charset=utf-8");
            })
            .RequireAuthorization()
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>The look stylesheet of a poll page (<c>/p/{id}/look.css</c>): exempt from the poll pages' <c>no-store</c>.</summary>
    public static bool IsPollLookPath(PathString path) =>
        path.StartsWithSegments("/p") && path.Value is { } v && v.EndsWith("/look.css", StringComparison.Ordinal) && v.Count(c => c == '/') == 3;
}
