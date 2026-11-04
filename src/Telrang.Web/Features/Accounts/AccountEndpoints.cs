using Telrang.Web.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Telrang.Web.Features.Accounts;

/// <summary>Self-service POST endpoints that are not forms re-rendering a page.</summary>
public static class AccountEndpoints
{
    /// <summary>Form field holding the password or authenticator code for re-authentication.</summary>
    public const string ReauthField = "Secret";

    public static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var manage = endpoints.MapGroup("/Account/Manage").RequireAuthorization();

        // "Download my data" (JSON).
        manage.MapPost("/DownloadPersonalData", async (
            HttpContext http,
            [FromServices] IAntiforgery antiforgery,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] SignInManager<ApplicationUser> signIn,
            [FromServices] AccountService accounts) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Microsoft.AspNetCore.Http.Results.BadRequest();
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Microsoft.AspNetCore.Http.Results.NotFound();
            // Re-authentication: password or authenticator code.
            var form = await http.Request.ReadFormAsync();
            if (!await accounts.ReauthenticateAsync(user, form[ReauthField]))
            {
                (signIn as Security.AppSignInManager)?.RecordFailure();
                return Microsoft.AspNetCore.Http.Results.LocalRedirect("~/Account/Manage/PersonalData?reauth=failed");
            }
            http.Response.Headers.CacheControl = "no-store";
            return Microsoft.AspNetCore.Http.Results.File(await accounts.ExportAsync(user), "application/json", "my-data.json");
        }).DisableAntiforgery();

        // "Log out everywhere": new security stamp, so every cookie and circuit of this account dies
        // at its next validation; this browser is signed out now.
        manage.MapPost("/LogOutEverywhere", async (
            HttpContext http,
            [FromServices] IAntiforgery antiforgery,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] SignInManager<ApplicationUser> signIn) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Microsoft.AspNetCore.Http.Results.BadRequest();
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Microsoft.AspNetCore.Http.Results.NotFound();
            await users.UpdateSecurityStampAsync(user);
            await signIn.SignOutAsync();
            return Microsoft.AspNetCore.Http.Results.LocalRedirect("~/Account/Login");
        }).DisableAntiforgery();
    }
}
