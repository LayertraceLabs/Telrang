using Telrang.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Security;

/// <summary>
/// SignInManager that refuses deleted, pending and unverified self-registered accounts and records <see cref="ApplicationUser.LastLoginAt"/>
/// on every real sign-in (password, 2FA, recovery code, passkey, registration), not on refreshes.
/// Every real sign-in and every sign-out also drops the link-mode <c>vk</c> voter cookie, so the next
/// person on a shared browser does not see or edit the previous person's ballots (a fresh key is
/// minted on the next vote page visit).
/// </summary>
public sealed class AppSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    TimeProvider time,
    AbuseTracker abuse,
    Telrang.Web.Features.Status.AppMetrics metrics)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    private bool refreshing;

    public override async Task<bool> CanSignInAsync(ApplicationUser user) =>
        user.DeletedAt is null && !user.PendingSetup && !AwaitsVerification(user) && await base.CanSignInAsync(user);

    /// <summary>
    /// A self-registered account whose email is not verified yet: it cannot sign in (pentest R2-ACC-3), and is deleted
    /// after <c>AccountService.UnverifiedLifetime</c>. Admin-created, invited and setup accounts are not self-registered.
    /// </summary>
    public static bool AwaitsVerification(ApplicationUser user) => user.SelfRegistered && !user.EmailConfirmed;

    /// <summary>
    /// A failed sign-in (wrong password, 2FA or recovery code, unknown account): counted for the Status
    /// tab and as an abuse signal for the client address. Not for the account: someone else's failures
    /// must not block its owner (Identity's lockout covers the account).
    /// </summary>
    public void RecordFailure()
    {
        metrics.FailedLogins.Add(1);
        if (CurrentContext() is { } http) abuse.Record(ClientKeys.ForAbuse(http), null, AbuseSignal.FailedLogin);
    }

    private HttpContext? CurrentContext()
    {
        try { return Context; }
        catch (InvalidOperationException) { return null; } // no request (service-level callers)
    }

    private SignInResult Track(SignInResult result)
    {
        if (!result.Succeeded && !result.RequiresTwoFactor && !result.IsNotAllowed) RecordFailure();
        return result;
    }

    public override async Task<SignInResult> CheckPasswordSignInAsync(ApplicationUser user, string password, bool lockoutOnFailure) =>
        Track(await base.CheckPasswordSignInAsync(user, password, lockoutOnFailure));

    public override async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient) =>
        Track(await base.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient));

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode) =>
        Track(await base.TwoFactorRecoveryCodeSignInAsync(recoveryCode));

    public override async Task RefreshSignInAsync(ApplicationUser user)
    {
        refreshing = true;
        try { await base.RefreshSignInAsync(user); }
        finally { refreshing = false; }
    }

    public override async Task SignOutAsync()
    {
        await base.SignOutAsync();
        PollCookies.ForgetVoterKey(Context);
    }

    public override async Task SignInWithClaimsAsync(ApplicationUser user, AuthenticationProperties? authenticationProperties, IEnumerable<System.Security.Claims.Claim> additionalClaims)
    {
        await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
        if (!refreshing)
        {
            PollCookies.ForgetVoterKey(Context);
            user.LastLoginAt = time.GetUtcNow();
            await UserManager.UpdateAsync(user);
        }
    }
}
