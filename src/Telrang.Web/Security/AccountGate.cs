using System.Security.Claims;
using Telrang.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Security;

/// <summary>
/// Adds the <see cref="AccountGate.ClaimType"/> claim for accounts that must finish setup, so the
/// gate needs no database round trip per render. Refresh the sign-in after clearing the flags.
/// Also the header's display name and "hide name" flag (DESIGN.md §24.2, <see cref="Components.Layout.HeaderIdentity"/>):
/// display only, never used for an access decision.
/// </summary>
public sealed class AppClaimsPrincipalFactory(UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.PendingSetup) identity.AddClaim(new Claim(AccountGate.ClaimType, AccountGate.PendingSetup));
        else if (user.MustEnrolTwoFactor) identity.AddClaim(new Claim(AccountGate.ClaimType, AccountGate.EnrolTwoFactor));
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            identity.AddClaim(new Claim(Components.Layout.HeaderIdentity.DisplayNameClaim, user.DisplayName.Trim()));
        if (user.HideIdentity) identity.AddClaim(new Claim(Components.Layout.HeaderIdentity.HideClaim, "1"));
        return identity;
    }
}

/// <summary>
/// "Must finish setup" gate (DESIGN.md §14.3, §15): a signed-in user who still has to enrol an
/// authenticator app can only reach the enrolment page, logout, email confirmation, setup and
/// static files. Enforced as the <see cref="NotGatedRequirement"/>, which is part of the default
/// policy (every [Authorize] page and endpoint), the Admin policy and the fallback policy (every
/// endpoint without authorization metadata). Interactive navigation never passes through HTTP
/// middleware, but <c>AuthorizeRouteView</c> evaluates the page's policies on every render, so the
/// pages that are interactive and otherwise anonymous carry <see cref="AppPolicies.NotGated"/>
/// explicitly. Pages a gated user must reach opt out with [AllowAnonymous].
/// </summary>
public static class AccountGate
{
    public const string ClaimType = "av:gate";
    public const string PendingSetup = "setup";
    public const string EnrolTwoFactor = "2fa";
    public const string EnrolPath = "/Account/Manage/EnableAuthenticator";

    public static bool IsGated(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && user.HasClaim(c => c.Type == ClaimType);

    /// <summary>Lockout end of an admin lock (<c>AdminService.LockAsync</c>); failed sign-ins lock for minutes only.</summary>
    public static readonly DateTimeOffset AdminLockEnd = DateTimeOffset.MaxValue;

    /// <summary>
    /// The one "may this account act" check (database, for service entry points: a long-lived circuit can carry a
    /// principal from before the account was gated, locked or deleted). True when the account exists, is not deleted,
    /// pending, gated or locked by an admin, and, with <paramref name="verifiedEmail"/>, has a verified email (create
    /// polls, respond in <c>voters: accounts</c>, notify-me, results-ready mail, transfer target). A lockout after failed
    /// sign-ins does not count: anyone can cause one, and it must not freeze the owner's live sessions (pentest R2-ACC-2).
    /// </summary>
    public static async Task<bool> MayActAsync(ApplicationDbContext db, string userId, CancellationToken ct = default, bool verifiedEmail = false)
    {
        var row = await db.Users.Where(u => u.Id == userId && !u.PendingSetup && !u.MustEnrolTwoFactor && u.DeletedAt == null
                                            && (!verifiedEmail || (u.EmailConfirmed && u.Email != null)))
            .Select(u => new { u.LockoutEnd }).FirstOrDefaultAsync(ct);
        return row is not null && row.LockoutEnd != AdminLockEnd; // LockoutEnd is text in SQLite: compared here
    }
}

/// <summary>Fails while the principal carries the <see cref="AccountGate.ClaimType"/> claim. Anonymous users pass.</summary>
public sealed class NotGatedRequirement : AuthorizationHandler<NotGatedRequirement>, IAuthorizationRequirement
{
    public static readonly NotGatedRequirement Instance = new();

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NotGatedRequirement requirement)
    {
        if (!AccountGate.IsGated(context.User)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// HTTP side of the gate: a request refused only because the account is gated is sent to the
/// enrolment page (303, so a gated POST becomes a GET) instead of the access-denied page. A
/// pending-setup session (should not exist: such accounts have no password) is signed out.
/// </summary>
public sealed class GateAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Succeeded && AccountGate.IsGated(context.User)
            && result.AuthorizationFailure?.FailedRequirements.OfType<NotGatedRequirement>().Any() == true)
        {
            if (context.User.FindFirstValue(AccountGate.ClaimType) == AccountGate.PendingSetup)
            {
                await context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>().SignOutAsync();
                context.Response.Redirect("/Account/Login");
                return;
            }
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = AccountGate.EnrolPath;
            return;
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
