using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Telrang.Web.Data;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    // These endpoints are required by the Identity Razor components defined in the /Components/Account/Pages directory of this project.
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        // No external login providers are registered: the template's ExternalLogin pages and their PerformExternalLogin /
        // LinkExternalLogin endpoints are removed (pentest AV-15c; they would skip 2FA and auto-create accounts if a
        // provider were ever added).

        accountGroup.MapPost("/Logout", async (
            ClaimsPrincipal user,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string returnUrl) =>
        {
            await signInManager.SignOutAsync();
            // "~/" + a value starting with / or \ would leave the site; LocalRedirect then threw (pentest AV-15b).
            var offSite = string.IsNullOrEmpty(returnUrl) || returnUrl[0] is '/' or '\\' || returnUrl.Any(char.IsControl);
            return TypedResults.LocalRedirect(offSite ? "~/" : $"~/{returnUrl}");
        }).AllowAnonymous(); // reachable while the account gate is up

        accountGroup.MapPost("/PasskeyCreationOptions", async (
            HttpContext context,
            [FromServices] UserManager<ApplicationUser> userManager,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromServices] IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
            {
                return Results.NotFound($"Unable to load user with ID '{userManager.GetUserId(context.User)}'.");
            }

            var userId = await userManager.GetUserIdAsync(user);
            var userName = await userManager.GetUserNameAsync(user) ?? "User";
            var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new()
            {
                Id = userId,
                Name = userName,
                DisplayName = userName
            });
            return TypedResults.Content(optionsJson, contentType: "application/json");
        });

        accountGroup.MapPost("/PasskeyRequestOptions", async (
            HttpContext context,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromServices] IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);

            // Never for a named user (pentest R2-ACC-3): its allowCredentials would tell who has a passkey. The browser
            // offers its discoverable credentials for this site instead.
            var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(null);
            return TypedResults.Content(optionsJson, contentType: "application/json");
        });

        // DownloadPersonalData lives in Features/Accounts/AccountEndpoints (app-specific export).

        return accountGroup;
    }
}
