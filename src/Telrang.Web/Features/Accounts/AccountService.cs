using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telrang.Web.Data;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Accounts;

public sealed record LinkMatch(SetupLink Link, ApplicationUser User);

public sealed record SetupOutcome(IdentityResult Result, ApplicationUser? User = null);

public enum AdminSetupStatus { Ok, NotFound, WrongKey, Invalid }

public sealed record AdminSetupOutcome(AdminSetupStatus Status, string? Error = null, ApplicationUser? User = null);

/// <summary>
/// Account lifecycle (DESIGN.md §14.3, §14.6): admin bootstrap, setup links, password recovery,
/// email verification and change, soft delete, data export. Scoped: shares the request's
/// <see cref="ApplicationDbContext"/> with Identity's stores, so resolve it from a fresh scope per
/// action on interactive pages.
/// </summary>
public sealed class AccountService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    IMailQueue mail,
    SettingsService settings,
    SetupKey setupKey,
    RecoveryKey recoveryKey,
    TimeProvider time,
    ILogger<AccountService> logger,
    Results.PollChangeNotifier notifier,
    EmailProtection emails)
{
    public static readonly TimeSpan SetupLinkLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan AdminResetLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan SelfResetLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SelfResetCooldown = TimeSpan.FromMinutes(15);

    // Identity's UserStoreBase keeps the authenticator key and recovery codes as user tokens.
    private const string IdentityTokenProvider = "[AspNetUserStore]";
    private static readonly SemaphoreSlim AdminSetupLock = new(1, 1);
    private static readonly EmailAddressAttribute EmailFormat = new();

    private string AppName => settings.AppName;

    /// <summary>Base URL for a link shown on the page (see <see cref="SettingsService.LinkBase"/>); never for mailed links.</summary>
    public string LinkBase(string requestBaseUri) => settings.LinkBase(requestBaseUri);

    public static string SetupUrl(string baseUri, string token) => baseUri + "setup/account/" + token;
    public static string ResetUrl(string baseUri, string token) => baseUri + "Account/ResetPassword?token=" + token;

    // ---- admin bootstrap (/setup) ---------------------------------------------------------------

    /// <summary>
    /// Creates the first admin from the setup key: email verified (the key proves control of the
    /// server), must enrol TOTP before the session is usable. Serialised, so two correct submissions
    /// cannot both create an admin.
    /// </summary>
    public async Task<AdminSetupOutcome> CreateFirstAdminAsync(string? key, string email, string password, CancellationToken ct = default)
    {
        await AdminSetupLock.WaitAsync(ct);
        try
        {
            if (!setupKey.IsLive || await AdminBootstrapper.AdminExistsAsync(db, ct)) return new(AdminSetupStatus.NotFound);
            if (!setupKey.Check(key)) return new(AdminSetupStatus.WrongKey, "Setup key is wrong or expired.");

            email = email.Trim();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, MustEnrolTwoFactor = true, CreatedAt = time.GetUtcNow() };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded) return new(AdminSetupStatus.Invalid, Errors(created));
            var role = await users.AddToRoleAsync(user, AppRoles.Admin);
            if (!role.Succeeded) return new(AdminSetupStatus.Invalid, Errors(role));

            setupKey.Revoke();
            logger.LogWarning("Admin account created with the setup key."); // no user id at Warning (R3-DEP-2)
            return new(AdminSetupStatus.Ok, User: user);
        }
        finally
        {
            AdminSetupLock.Release();
        }
    }

    // ---- admin recovery (/setup/recover) -----------------------------------------------------------

    /// <summary>Non-deleted admin accounts (id, email), for the recovery page once the key is checked.</summary>
    public async Task<IReadOnlyList<(string Id, string Email)>> ListAdminsAsync(CancellationToken ct = default)
    {
        var rows = await (from ur in db.UserRoles
                          join r in db.Roles on ur.RoleId equals r.Id
                          join u in db.Users on ur.UserId equals u.Id
                          where r.Name == AppRoles.Admin && u.DeletedAt == null && u.Email != null
                          select new { u.Id, u.Email }).ToListAsync(ct);
        return rows.Select(r => (r.Id, emails.Display(r.Email)!)).OrderBy(r => r.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool CheckRecoveryKey(string? key) => recoveryKey.IsLive && recoveryKey.Check(key);

    /// <summary>
    /// Admin recovery with the startup recovery key: new password for the chosen
    /// admin, 2FA cleared (authenticator key and recovery codes removed; enrolment is required again at
    /// sign-in, since admins must keep 2FA), passkeys removed (pentest AV-06), lockout cleared, every session ended.
    /// Uses up the key.
    /// </summary>
    public async Task<AdminSetupOutcome> RecoverAdminAsync(string? key, string adminId, string password, CancellationToken ct = default)
    {
        await AdminSetupLock.WaitAsync(ct);
        try
        {
            if (!recoveryKey.IsLive) return new(AdminSetupStatus.NotFound);
            if (!recoveryKey.Check(key)) return new(AdminSetupStatus.WrongKey, "Recovery key is wrong or expired.");
            var user = await users.FindByIdAsync(adminId);
            if (user is null || user.DeletedAt is not null || !await users.IsInRoleAsync(user, AppRoles.Admin))
                return new(AdminSetupStatus.Invalid, "Pick an admin account.");

            await using var tx = await db.Database.BeginWriteAsync(ct);
            var reset = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
            if (!reset.Succeeded) return new(AdminSetupStatus.Invalid, Errors(reset));
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.RemoveAuthenticationTokenAsync(user, IdentityTokenProvider, "AuthenticatorKey");
            await users.RemoveAuthenticationTokenAsync(user, IdentityTokenProvider, "RecoveryCodes");
            await RemovePasskeysAsync(user);
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            user.MustEnrolTwoFactor = true;
            await users.UpdateSecurityStampAsync(user); // saves the flag too; other sessions end
            await Admin.AdminLog.WriteAsync(db, time, emails, user.Id, "admin.recover", "user", user.Id, "recovery key", ct);
            await tx.CommitAsync(ct);

            recoveryKey.Revoke();
            logger.LogWarning("Admin account recovered with the recovery key. Remove App__RecoveryKey now.");
            return new(AdminSetupStatus.Ok, User: user);
        }
        finally
        {
            AdminSetupLock.Release();
        }
    }

    // ---- setup links ------------------------------------------------------------------------------

    /// <summary>Issues a single-use link; older unused links of the same kind for this user die.</summary>
    public Task<string> IssueLinkAsync(ApplicationUser user, SetupLinkPurpose purpose, TimeSpan lifetime, string? createdBy, CancellationToken ct = default) =>
        IssueLinkAsync(user, purpose, lifetime, createdBy, SecretTokens.Generate(), ct);

    private async Task<string> IssueLinkAsync(ApplicationUser user, SetupLinkPurpose purpose, TimeSpan lifetime, string? createdBy,
        (string Token, string Hash) secret, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var kinds = purpose == SetupLinkPurpose.PasswordReset
            ? new[] { SetupLinkPurpose.PasswordReset }
            : new[] { SetupLinkPurpose.AccountSetup, SetupLinkPurpose.EmailInvite };
        await db.SetupLinks.Where(l => l.UserId == user.Id && l.UsedAt == null && kinds.Contains(l.Purpose))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UsedAt, now), ct);

        var (token, hash) = secret;
        db.SetupLinks.Add(new SetupLink
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = hash,
            Purpose = purpose,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            CreatedBy = createdBy,
            SecurityStamp = purpose == SetupLinkPurpose.PasswordReset ? await users.GetSecurityStampAsync(user) : null,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>The live link for <paramref name="token"/>, or null (malformed, unknown, used, expired, wrong purpose, stale).</summary>
    public async Task<LinkMatch?> FindLinkAsync(string? token, IReadOnlyCollection<SetupLinkPurpose> purposes, CancellationToken ct = default)
    {
        if (!SecretTokens.TryHash(token, out var hash)) return null;
        var link = await db.SetupLinks.SingleOrDefaultAsync(l => l.TokenHash == hash, ct);
        if (link is null || link.UsedAt is not null || !purposes.Contains(link.Purpose) || link.ExpiresAt <= time.GetUtcNow()) return null;
        var user = await users.FindByIdAsync(link.UserId);
        if (user is null || user.DeletedAt is not null) return null;
        if (link.Purpose == SetupLinkPurpose.PasswordReset)
        {
            // Any password change (or other security-stamp change) since issue invalidates the link.
            if (user.PendingSetup || link.SecurityStamp != await users.GetSecurityStampAsync(user)) return null;
        }
        else if (!user.PendingSetup)
        {
            return null;
        }
        return new LinkMatch(link, user);
    }

    // ---- admin-created accounts -------------------------------------------------------------------

    /// <summary>
    /// Creates a pending account (no password; UserName = its GUID) and its setup link. With
    /// <paramref name="inviteEmail"/> the address is stored (unverified) and the link is an invite.
    /// </summary>
    public async Task<(IdentityResult Result, ApplicationUser? User, string? Token)> CreatePendingAsync(string? inviteEmail, string adminId, CancellationToken ct = default)
    {
        var user = new ApplicationUser { PendingSetup = true, CreatedAt = time.GetUtcNow() };
        user.UserName = user.Id;
        if (!string.IsNullOrWhiteSpace(inviteEmail)) user.Email = inviteEmail.Trim();
        var result = await users.CreateAsync(user);
        if (!result.Succeeded) return (result, null, null);
        var token = await IssueLinkAsync(user, user.Email is null ? SetupLinkPurpose.AccountSetup : SetupLinkPurpose.EmailInvite, SetupLinkLifetime, adminId, ct);
        return (result, user, token);
    }

    /// <summary>Mails the setup link for <paramref name="token"/>, built from the Public URL only (false when not queued).</summary>
    public async Task<bool> SendInviteAsync(string email, string token, CancellationToken ct = default) =>
        settings.MailLinkBase is { } baseUri
        && await mail.EnqueueAsync(email, $"Your {AppName} account",
            $"An account on {AppName} was created for you. Set your password here (valid 7 days):\n\n{SetupUrl(baseUri, token)}\n", MailType.AccountInvite, ct: ct);

    /// <summary>Completes a setup link: sets email (unless invited), password; TOTP enrolment follows.</summary>
    public async Task<SetupOutcome> CompleteSetupAsync(string? token, string? email, string password, CancellationToken ct = default)
    {
        var match = await FindLinkAsync(token, [SetupLinkPurpose.AccountSetup, SetupLinkPurpose.EmailInvite], ct);
        if (match is null) return new(IdentityResult.Failed(new IdentityError { Code = "InvalidLink", Description = "This link is invalid or expired." }));
        var (link, user) = (match.Link, match.User);

        if (link.Purpose == SetupLinkPurpose.AccountSetup)
        {
            email = email?.Trim();
            if (string.IsNullOrEmpty(email) || !EmailFormat.IsValid(email))
                return new(IdentityResult.Failed(users.ErrorDescriber.InvalidEmail(email)));
            user.Email = email; // plain here; the user store encrypts it on save (DESIGN.md §30)
            // §22.1: with mail off there is no way to check the address; the admin may accept it as typed.
            var current = settings.Current;
            user.EmailConfirmed = !current.Mail.IsConfigured && current.AutoVerifyWithoutMail;
        }
        else
        {
            // The invite was mailed to this address, so opening the link proves it.
            user.EmailConfirmed = true;
            email = emails.Reveal(user.Email);
            if (email is null) return new(IdentityResult.Failed(users.ErrorDescriber.InvalidEmail(email)));
        }
        user.UserName = email;
        user.PendingSetup = false;
        user.MustEnrolTwoFactor = true;

        await using var tx = await db.Database.BeginWriteAsync(ct);
        var result = await users.AddPasswordAsync(user, password);
        if (!result.Succeeded) return new(result);
        link.UsedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (!user.EmailConfirmed) await SendVerificationAsync(user, ct);
        return new(IdentityResult.Success, user);
    }

    // ---- password reset ---------------------------------------------------------------------------

    /// <summary>
    /// Forgot-password request. Same work and same (absent) answer whether or not the address
    /// belongs to an account: the lookup happens, the response never depends on it. Only accounts
    /// with a verified email get a mail. Rules:
    /// <list type="bullet">
    /// <item>At most one reset link per account per 15 minutes, counting admin-issued links too; a
    /// request inside the cooldown does nothing (an admin's fresh link stays valid).</item>
    /// <item>The new link replaces older ones only when its mail was actually queued; if the outbox
    /// drops it (per-type cooldown, daily cap), no link is created and older links stay valid.</item>
    /// </list>
    /// </summary>
    public async Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
    {
        if (settings.MailLinkBase is not { } baseUri) return;
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not { EmailConfirmed: true, DeletedAt: null, PendingSetup: false } || emails.Reveal(user.Email) is not { } address) return;
        var since = time.GetUtcNow() - SelfResetCooldown;
        if (await db.SetupLinks.AnyAsync(l => l.UserId == user.Id && l.Purpose == SetupLinkPurpose.PasswordReset && l.CreatedAt > since, ct))
            return;
        var secret = SecretTokens.Generate();
        var queued = await mail.EnqueueAsync(address, $"Reset your {AppName} password",
            $"Set a new password here (valid 30 minutes, single use):\n\n{ResetUrl(baseUri, secret.Token)}\n\nIf you did not ask for this, ignore this message.",
            MailType.PasswordReset, ct: ct);
        if (!queued) return;
        await IssueLinkAsync(user, SetupLinkPurpose.PasswordReset, SelfResetLifetime, createdBy: null, secret, ct);
    }

    /// <summary>
    /// Admin-issued reset link (24 h). Returns the URL to show once (so it always replaces older reset
    /// links: the admin has it in hand; built like any shown link); also mailed, from the Public URL only, when the
    /// outbox accepts it.
    /// </summary>
    public async Task<string> IssueAdminResetAsync(ApplicationUser user, string adminId, string requestBaseUri, CancellationToken ct = default)
    {
        var token = await IssueLinkAsync(user, SetupLinkPurpose.PasswordReset, AdminResetLifetime, adminId, ct);
        if (emails.Reveal(user.Email) is { } address && settings.Current.Mail.IsConfigured && settings.MailLinkBase is { } mailBase)
        {
            await mail.EnqueueAsync(address, $"Reset your {AppName} password",
                $"An administrator issued a password reset for your account. Set a new password here (valid 24 hours, single use):\n\n{ResetUrl(mailBase, token)}\n",
                MailType.PasswordReset, ct: ct);
        }
        return ResetUrl(LinkBase(requestBaseUri), token);
    }

    /// <summary>
    /// Uses a reset link: new password, lockout cleared, security stamp bumped, 2FA kept, passkeys removed (pentest
    /// AV-06: a passkey signs in without the password, so one planted by an intruder would survive the reset).
    /// </summary>
    public async Task<SetupOutcome> ResetPasswordAsync(string? token, string newPassword, CancellationToken ct = default)
    {
        var match = await FindLinkAsync(token, [SetupLinkPurpose.PasswordReset], ct);
        if (match is null) return new(IdentityResult.Failed(new IdentityError { Code = "InvalidLink", Description = "This link is invalid or expired." }));
        var user = match.User;

        await using var tx = await db.Database.BeginWriteAsync(ct);
        // Identity's own reset validates the password and bumps the security stamp in one step.
        var identityToken = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, identityToken, newPassword);
        if (!result.Succeeded) return new(result);
        var now = time.GetUtcNow();
        await db.SetupLinks.Where(l => l.UserId == user.Id && l.UsedAt == null && l.Purpose == SetupLinkPurpose.PasswordReset)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UsedAt, now), ct);
        await RemovePasskeysAsync(user);
        // Failed-attempt lockouts end; an admin lock (end = max) stays.
        if (user.LockoutEnd is { } end && end != DateTimeOffset.MaxValue) await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await tx.CommitAsync(ct);
        return new(IdentityResult.Success, user);
    }

    /// <summary>Removes every passkey of the account (recovery, reset, admin "disable 2FA"). Returns how many.</summary>
    public async Task<int> RemovePasskeysAsync(ApplicationUser user)
    {
        var passkeys = await users.GetPasskeysAsync(user);
        foreach (var passkey in passkeys) await users.RemovePasskeyAsync(user, passkey.CredentialId);
        return passkeys.Count;
    }

    // ---- email verification and change ------------------------------------------------------------

    /// <summary>Queues a verification mail. Null when there is nothing to verify; else the outbox's answer.</summary>
    public async Task<MailEnqueueResult?> SendVerificationAsync(ApplicationUser user, CancellationToken ct = default)
    {
        if (emails.Reveal(user.Email) is not { } address || user.EmailConfirmed) return null;
        if (settings.MailLinkBase is not { } baseUri) return new MailEnqueueResult(false, MailDropReason.NotConfigured);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
        var url = $"{baseUri}Account/ConfirmEmail?userId={Uri.EscapeDataString(user.Id)}&code={code}";
        // Names the account's creation time (pentest R2-ACC-1): someone who did not register then knows it is not theirs.
        return await mail.TryEnqueueAsync(address, $"Confirm your email for {AppName}",
            $"Confirm this address for the {AppName} account created on {PollTime.Format(user.CreatedAt, user.TimeZone)}. " +
            $"The page asks for that account's password.\n\n{url}\n\nIf you did not create this account, ignore this message.",
            MailType.Verification, ct: ct);
    }

    /// <summary>
    /// Mails a confirmation link to the new address. The switch happens when it is opened. Null when
    /// nothing was attempted (same or taken address: the page answers as if sent, no oracle); else the
    /// outbox's answer.
    /// </summary>
    public async Task<MailEnqueueResult?> RequestEmailChangeAsync(ApplicationUser user, string newEmail, CancellationToken ct = default)
    {
        if (settings.MailLinkBase is not { } baseUri) return new MailEnqueueResult(false, MailDropReason.NotConfigured);
        newEmail = newEmail.Trim();
        if (string.Equals(newEmail, emails.Reveal(user.Email), StringComparison.OrdinalIgnoreCase)) return null;
        // Taken by someone else: say nothing different (no oracle), send nothing.
        if (await users.FindByEmailAsync(newEmail) is not null) return null;
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateChangeEmailTokenAsync(user, newEmail)));
        var url = $"{baseUri}Account/ConfirmEmailChange?userId={Uri.EscapeDataString(user.Id)}&email={Uri.EscapeDataString(newEmail)}&code={code}";
        return await mail.TryEnqueueAsync(newEmail, $"Confirm your new email for {AppName}",
            $"Confirm this address as the new login for your {AppName} account:\n\n{url}\n\nIf you did not ask for this, ignore this message.",
            MailType.EmailChange, ct: ct);
    }

    /// <summary>Applies a confirmed email change (email = login name) and notifies the old address.</summary>
    public async Task<IdentityResult> ConfirmEmailChangeAsync(ApplicationUser user, string newEmail, string code, CancellationToken ct = default)
    {
        var oldEmail = emails.Reveal(user.Email);
        string decoded;
        try { decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return IdentityResult.Failed(new IdentityError { Code = "InvalidToken", Description = "This link is invalid or expired." }); }

        await using var tx = await db.Database.BeginWriteAsync(ct);
        var changed = await users.ChangeEmailAsync(user, newEmail, decoded);
        if (!changed.Succeeded) return changed;
        var renamed = await users.SetUserNameAsync(user, newEmail);
        if (!renamed.Succeeded) return renamed;
        await tx.CommitAsync(ct);

        if (oldEmail is not null && !string.Equals(oldEmail, newEmail, StringComparison.OrdinalIgnoreCase))
        {
            await mail.EnqueueAsync(oldEmail, $"Your {AppName} email was changed",
                $"The login email of your {AppName} account was changed to {newEmail}. If this was not you, contact the administrator.",
                MailType.EmailChangeNotice, ct: ct);
        }
        return IdentityResult.Success;
    }

    /// <summary>
    /// Tells the account's verified address that a sign-in credential was added or replaced (pentest AV-02), so a stolen
    /// session that mints one does not go unnoticed. Queued only while mail is usable.
    /// </summary>
    public Task NotifySecurityChangeAsync(ApplicationUser user, string what, CancellationToken ct = default) =>
        user.EmailConfirmed && emails.Reveal(user.Email) is { } email
            ? mail.EnqueueAsync(email, $"{what} on your {AppName} account",
                $"{what} on your {AppName} account ({time.GetUtcNow().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC).\n\n" +
                "If this was not you, sign in, change your password, check Passkeys and Two-factor authentication on your " +
                "account page, and use \"Log out everywhere\". If you cannot sign in, contact the administrator.",
                MailType.SecurityNotice, ct: ct)
            : Task.FromResult(false);

    /// <summary>
    /// A registration for an address that already has an account (pentest AV-10): the form answers as for a new one, and
    /// the address gets a note instead. Verified owners are pointed to sign-in and password reset; an unverified
    /// self-registered account (possibly someone squatting the address) is said to expire. Pending invites get nothing.
    /// </summary>
    public async Task NotifyRegistrationAttemptAsync(string email, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not { DeletedAt: null, PendingSetup: false } || emails.Reveal(user.Email) is not { } to || settings.MailLinkBase is not { } baseUri) return;
        var body = user.EmailConfirmed
            ? $"Someone tried to register a new {AppName} account with this address. You already have one: log in at " +
              $"{baseUri}Account/Login, or set a new password at {baseUri}Account/ForgotPassword.\n\nIf this was not you, ignore this message."
            : $"Someone tried to register a {AppName} account with this address, which already has an unverified account. " +
              $"If you did not create it, ignore this message: unverified accounts are deleted {UnverifiedLifetime.Days} days " +
              "after registration, and the address can register again then.";
        await mail.EnqueueAsync(to, $"Registration attempt on {AppName}", body, MailType.RegistrationAttempt, ct: ct);
    }

    /// <summary>Self-registered accounts that never verified their email are deleted after this (pentest AV-10).</summary>
    public static readonly TimeSpan UnverifiedLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Soft-deletes self-registered accounts whose email is still unverified <see cref="UnverifiedLifetime"/> after
    /// creation, so nobody can hold an address they do not own. Admin-created and admin accounts are not touched.
    /// Returns how many. Run by the daily pass of <see cref="PollSweepService"/>.
    /// </summary>
    public async Task<int> PurgeUnverifiedAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - UnverifiedLifetime;
        // CreatedAt is stored as text; compare in memory (few rows: unverified self-registrations only).
        var candidates = await db.Users.Where(u => u.SelfRegistered && !u.EmailConfirmed && u.DeletedAt == null && u.Email != null)
            .ToListAsync(ct);
        var purged = 0;
        foreach (var user in candidates.Where(u => u.CreatedAt < cutoff))
        {
            if (await users.IsInRoleAsync(user, AppRoles.Admin)) continue;
            await SoftDeleteAsync(user, ct);
            purged++;
        }
        if (purged > 0) logger.LogInformation("Deleted {Count} self-registered accounts that were not verified within {Days} days.", purged, UnverifiedLifetime.Days);
        return purged;
    }

    // ---- re-authentication -------------------------------------------------------------------------

    /// <summary>
    /// Re-authentication for sensitive self-service actions (delete account, download data, 2FA off, adding a passkey,
    /// new recovery codes, changing the email): the account's password, or a current authenticator code when 2FA is on.
    /// Failures count towards the account's lockout like failed sign-ins, and a locked-out account cannot re-authenticate
    /// (pentest R2-ACC-4: a stolen session must not guess the password or a code from many addresses). A success resets
    /// nothing.
    /// </summary>
    public Task<bool> ReauthenticateAsync(ApplicationUser user, string? secret) =>
        CountedAsync(user, async () =>
        {
            secret = secret?.Trim();
            if (string.IsNullOrEmpty(secret) || secret.Length > 256) return false;
            if (await users.HasPasswordAsync(user) && await users.CheckPasswordAsync(user, secret)) return true;
            return await CheckTotpAsync(user, secret);
        });

    /// <summary>A current authenticator code (2FA must be on); failures count like <see cref="ReauthenticateAsync"/>'s.</summary>
    public Task<bool> VerifyTotpAsync(ApplicationUser user, string? code) => CountedAsync(user, () => CheckTotpAsync(user, code));

    private async Task<bool> CheckTotpAsync(ApplicationUser user, string? code)
    {
        code = code?.Replace(" ", "").Replace("-", "");
        if (string.IsNullOrEmpty(code) || code.Length > 10 || !await users.GetTwoFactorEnabledAsync(user)) return false;
        return await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
    }

    private async Task<bool> CountedAsync(ApplicationUser user, Func<Task<bool>> check)
    {
        if (await users.IsLockedOutAsync(user)) return false;
        if (await check()) return true;
        await users.AccessFailedAsync(user);
        return false;
    }

    // ---- deletion ---------------------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="user"/> is an admin and no other admin could act without them (pentest AV-15d): other
    /// admins count only when not deleted, pending, gated (<see cref="AccountGate.MayActAsync"/>) or locked. Call it
    /// inside <see cref="SerializeAdminChangeAsync{T}"/> together with the change it guards.
    /// </summary>
    public async Task<bool> IsLastAdminAsync(ApplicationUser user, CancellationToken ct = default)
    {
        if (!await users.IsInRoleAsync(user, AppRoles.Admin)) return false;
        var others = await (from ur in db.UserRoles
                            join r in db.Roles on ur.RoleId equals r.Id
                            join u in db.Users on ur.UserId equals u.Id
                            where r.Name == AppRoles.Admin && u.Id != user.Id && u.DeletedAt == null && !u.PendingSetup && !u.MustEnrolTwoFactor
                            select u.LockoutEnd).ToListAsync(ct);
        var now = time.GetUtcNow();
        return !others.Any(end => end is null || end <= now); // LockoutEnd is text in SQLite: compared here
    }

    /// <summary>
    /// Runs a change that could remove the last admin (demote, delete) with its <see cref="IsLastAdminAsync"/> check under
    /// the process-wide admin lock (also used by the setup and recovery flows), so two admins removing each other at the
    /// same time cannot both pass the check (single instance, DESIGN §9).
    /// </summary>
    public async Task<T> SerializeAdminChangeAsync<T>(Func<Task<T>> change, CancellationToken ct = default)
    {
        await AdminSetupLock.WaitAsync(ct);
        try { return await change(); }
        finally { AdminSetupLock.Release(); }
    }

    /// <summary>
    /// Soft delete (§14.3): own polls deleted with their ballots; email, password, 2FA, passkeys,
    /// logins and roles removed; UserName = Id; DeletedAt set; security stamp bumped. The row stays
    /// so references remain valid; the email can register again. Returns the number of polls deleted.
    /// </summary>
    public async Task<int> SoftDeleteAsync(ApplicationUser user, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginWriteAsync(ct);
        var pollIds = await db.Polls.Where(p => p.OwnerId == user.Id).Select(p => p.Id).ToListAsync(ct);
        await PollService.DeleteCascadeAsync(db, pollIds, ct);
        await db.SetupLinks.Where(l => l.UserId == user.Id).ExecuteDeleteAsync(ct);

        await RemovePasskeysAsync(user);
        foreach (var login in await users.GetLoginsAsync(user)) await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        await users.RemoveAuthenticationTokenAsync(user, IdentityTokenProvider, "AuthenticatorKey");
        await users.RemoveAuthenticationTokenAsync(user, IdentityTokenProvider, "RecoveryCodes");
        var roles = await users.GetRolesAsync(user);
        if (roles.Count > 0) await users.RemoveFromRolesAsync(user, roles);

        user.Email = null;
        user.NormalizedEmail = null;
        user.EmailConfirmed = false;
        user.UserName = user.Id;
        user.PasswordHash = null;
        user.PhoneNumber = null;
        user.TwoFactorEnabled = false;
        user.DisplayName = null;
        user.TimeZone = null;
        user.PendingSetup = false;
        user.MustEnrolTwoFactor = false;
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.DeletedAt = time.GetUtcNow();
        var result = await users.UpdateSecurityStampAsync(user); // also saves the fields above
        if (!result.Succeeded) throw new InvalidOperationException("Delete failed: " + Errors(result));
        await tx.CommitAsync(ct);
        foreach (var id in pollIds) notifier.End(id);
        logger.LogInformation("Account {UserId} deleted ({Polls} polls).", user.Id, pollIds.Count);
        return pollIds.Count;
    }

    // ---- export -----------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>"Download my data": profile, owned polls with their definitions, own ballots.</summary>
    public async Task<byte[]> ExportAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var polls = await db.Polls.AsNoTracking().Where(p => p.OwnerId == user.Id)
            .Select(p => new { p.Title, p.CreatedAt, p.UpdatedAt, p.ClosedAt, p.DefinitionJson, Ballots = p.Ballots.Count })
            .ToListAsync(ct);
        var ballotRows = await db.Ballots.AsNoTracking().Where(b => b.UserId == user.Id)
            .Select(b => new { b.Poll!.Title, b.Poll.Type, b.ResponseNo, b.DisplayName, b.SubmittedAt, b.UpdatedAt, b.PayloadJson })
            .ToListAsync(ct);
        var ballots = ballotRows.OrderBy(b => b.SubmittedAt).Select(b => new
        {
            poll = b.Title,
            type = b.Type.ToString().ToLowerInvariant(),
            response = b.ResponseNo,
            name = b.DisplayName,
            b.SubmittedAt,
            b.UpdatedAt,
            ballot = JsonSerializer.Deserialize<JsonElement>(b.PayloadJson),
        }).ToList();
        var export = new
        {
            exportedAt = time.GetUtcNow(),
            profile = new
            {
                Email = emails.Reveal(user.Email),
                user.EmailConfirmed,
                user.DisplayName,
                user.TimeZone,
                user.CreatedAt,
                user.LastLoginAt,
                TwoFactorEnabled = await users.GetTwoFactorEnabledAsync(user),
                Passkeys = (await users.GetPasskeysAsync(user)).Select(p => new { p.Name, p.CreatedAt }),
                Roles = await users.GetRolesAsync(user),
            },
            polls = polls.OrderBy(p => p.CreatedAt).Select(p => new { p.Title, p.CreatedAt, p.UpdatedAt, p.ClosedAt, BallotCount = p.Ballots, definition = JsonSerializer.Deserialize<JsonElement>(p.DefinitionJson) }),
            // Ballots recorded with this account (accounts/public mode, and invite links used while signed
            // in). Anonymous ballots are keyed by a browser cookie and cannot be attributed.
            ballots,
        };
        return JsonSerializer.SerializeToUtf8Bytes(export, ExportJson);
    }

    private static string Errors(IdentityResult r) => string.Join(" ", r.Errors.Select(e => e.Description));
}
