using System.Globalization;
using System.Security.Claims;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Admin;

public sealed record AdminPollRow(string EncodedId, string Title, string OwnerEmail, PollType Type, int BallotCount,
    bool IsClosed, bool ClosedManually, DateTimeOffset CreatedAt);

/// <summary>Per-account limit overrides (null = global default, 0 = no limit), in <see cref="Fields"/> order.</summary>
public sealed record UserLimitOverrides(int? ActivePolls, int? PollsPerDay, int? BallotsPerPoll, int? InvitesPerPoll, int? PollDurationDays,
    int? ResponsesPerVoter)
{
    /// <summary>(field, label, global setting key) for the admin form.</summary>
    public static readonly IReadOnlyList<(string Field, string Label, string SettingKey)> Fields =
    [
        (nameof(ActivePolls), "Open polls", SettingKeys.LimitActivePolls),
        (nameof(PollsPerDay), "New polls per day", SettingKeys.LimitPollsPerDay),
        (nameof(BallotsPerPoll), "Ballots per poll", SettingKeys.LimitBallotsPerPoll),
        (nameof(InvitesPerPoll), "Invite links per poll", SettingKeys.LimitInvitesPerPoll),
        (nameof(PollDurationDays), "Poll duration (days)", SettingKeys.LimitPollDurationDays),
        (nameof(ResponsesPerVoter), "Responses per voter", SettingKeys.LimitResponsesPerVoter),
    ];

    public int? Get(string field) => field switch
    {
        nameof(ActivePolls) => ActivePolls,
        nameof(PollsPerDay) => PollsPerDay,
        nameof(BallotsPerPoll) => BallotsPerPoll,
        nameof(InvitesPerPoll) => InvitesPerPoll,
        nameof(PollDurationDays) => PollDurationDays,
        nameof(ResponsesPerVoter) => ResponsesPerVoter,
        _ => null,
    };

    public bool Any => Fields.Any(f => Get(f.Field) is not null);
}

/// <param name="PendingLinkExpiresAt">Pending accounts: expiry of the live setup link (null = none live).</param>
/// <param name="PendingLinkBy">Pending accounts: email of the admin who issued that link.</param>
public sealed record AdminUserRow(string Id, string? Email, bool PendingSetup, bool EmailConfirmed, bool TwoFactorEnabled,
    bool MustEnrolTwoFactor, DateTimeOffset? LockoutEnd, int AccessFailedCount, IReadOnlyList<string> Roles, int PollCount,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt)
{
    public DateTimeOffset? PendingLinkExpiresAt { get; init; }
    public string? PendingLinkBy { get; init; }
    public UserLimitOverrides Limits { get; init; } = new(null, null, null, null, null, null);
    public string? DisplayName { get; init; }

    public bool IsLockedOut => LockoutEnd is { } end && end > DateTimeOffset.UtcNow;
    public bool IsAdmin => Roles.Contains(AppRoles.Admin);

    /// <summary>
    /// Why the account cannot be made admin, for the row (null = it can). Mirrors
    /// <see cref="AccountGate.MayActAsync"/> with a verified email, which <see cref="AdminService.SetAdminAsync"/> enforces.
    /// </summary>
    public string? AdminBlocker => PendingSetup ? "Pending setup"
        : Email is null || !EmailConfirmed ? "Unverified"
        : MustEnrolTwoFactor ? "2FA required"
        : null;
}

/// <summary>One page of the Users tab: at most <see cref="AdminService.MaxUserRows"/> rows of <see cref="Total"/> matches.</summary>
public sealed record AdminUserList(IReadOnlyList<AdminUserRow> Rows, int Total)
{
    public bool Truncated => Total > Rows.Count;
}

public sealed record AdminStats(int Users, int Polls, int OpenPolls, int Ballots);

/// <summary>Outcome of an admin action: <see cref="Ok"/> false means nothing changed. <see cref="Link"/> is a one-time URL to show.</summary>
public sealed record AdminOutcome(bool Ok, string Message, string? Link = null)
{
    public static readonly AdminOutcome Denied = new(false, "Not authorized.");
    public static readonly AdminOutcome NotFound = new(false, "Not found.");
}

/// <summary>
/// Admin operations (DESIGN.md §10, §14.3). Every method re-checks the Admin policy against the
/// caller's principal (a circuit can outlive the role) and writes the admin action log. Resolve it
/// from a fresh scope per action: it uses the scoped UserManager/DbContext, which would go stale
/// over a long-lived circuit.
/// </summary>
public sealed class AdminService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    PollAccessService access,
    AccountService accounts,
    SettingsService settings,
    IMailQueue mail,
    IAuthorizationService authorization,
    TimeProvider time,
    AbuseTracker abuse,
    MailSender mailSender,
    BlockedRangeService ranges,
    BrandingService branding,
    Results.PollChangeNotifier notifier,
    ClientInfo client,
    AuthSessionStore sessions,
    EmailProtection emails,
    ILogger<AdminService> logger)
{
    // Identity's UserStoreBase keeps the authenticator key and recovery codes as user tokens.
    private const string IdentityTokenProvider = "[AspNetUserStore]";

    /// <summary>
    /// Admin policy (role and not gated, from the principal) plus database checks that the account still has the admin
    /// role and is not gated, locked or deleted now: an interactive circuit, or a cookie inside its revalidation
    /// interval, can carry a principal from before that changed (pentest R2-ACC-2).
    /// </summary>
    public async Task<bool> IsAdminAsync(ClaimsPrincipal caller) =>
        (await authorization.AuthorizeAsync(caller, AppPolicies.Admin)).Succeeded
        && users.GetUserId(caller) is { } id
        && await AccountGate.MayActAsync(db, id)
        && await (from ur in db.UserRoles
                  join r in db.Roles on ur.RoleId equals r.Id
                  where ur.UserId == id && r.Name == AppRoles.Admin
                  select ur.UserId).AnyAsync();

    /// <summary>
    /// Writes the admin action log and the same line to the application log (pentest R2 Info c: the table keeps only the
    /// newest rows, the console log survives in the host's log store). The application log line names the action and
    /// account ids only: no notes (emails, poll titles), no poll ids (a poll's id is its link) and no blocked ranges
    /// (addresses).
    /// </summary>
    private async Task LogAsync(ClaimsPrincipal caller, string action, string? targetType = null, string? targetId = null, string? note = null)
    {
        var adminId = users.GetUserId(caller) ?? "?";
        await AdminLog.WriteAsync(db, time, emails, adminId, action, targetType, targetId, note);
        logger.LogInformation("Admin {AdminId}: {Action} {TargetType} {TargetId}", adminId, action, targetType,
            targetType is "user" or "block" ? targetId : null);
    }

    public async Task<AdminStats?> GetStatsAsync(ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return null;
        var now = time.GetUtcNow();
        var polls = await db.Polls.AsNoTracking().Select(p => new { p.ClosedAt, p.ClosesAt }).ToListAsync(ct);
        return new AdminStats(await db.Users.CountAsync(u => u.DeletedAt == null, ct), polls.Count,
            polls.Count(p => !PollState.IsClosed(p.ClosedAt, p.ClosesAt, now)), await db.Ballots.CountAsync(ct));
    }

    // ---- polls ----------------------------------------------------------------------------------

    /// <summary>All polls, newest first (listing, not a by-id lookup). Null when the caller is not an admin.</summary>
    public async Task<IReadOnlyList<AdminPollRow>?> ListPollsAsync(ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return null;
        var rows = await db.Polls.AsNoTracking()
            .Select(p => new { p.Id, p.Title, OwnerEmail = p.Owner!.Email, p.Type, Count = p.Ballots.Count, p.ClosedAt, p.ClosesAt, p.CreatedAt })
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows.Select(r => new AdminPollRow(PollId.Encode(r.Id), r.Title, emails.Display(r.OwnerEmail) ?? "(no email)", r.Type, r.Count,
                PollState.IsClosed(r.ClosedAt, r.ClosesAt, now), r.ClosedAt is not null, r.CreatedAt))
            .OrderByDescending(r => r.CreatedAt) // in memory: SQLite cannot ORDER BY DateTimeOffset
            .ToList();
    }

    public async Task<AdminOutcome> SetPollClosedAsync(ClaimsPrincipal caller, string encodedId, bool closed, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        var poll = await access.GetForAdminAsync(db, encodedId, tracking: true, ct);
        if (poll is null) return AdminOutcome.NotFound;
        poll.ClosedAt = closed ? poll.ClosedAt ?? time.GetUtcNow() : null;
        // ClosedNotifiedAt stays: it is the last results-ready mailing; the sweep mails again after a later close only
        // once 24 h have passed (pentest R2-POLLS-3).
        poll.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        notifier.Changed(poll.Id);
        await LogAsync(caller, closed ? "poll.close" : "poll.reopen", "poll", encodedId, poll.Title);
        return new AdminOutcome(true, closed ? $"Closed “{poll.Title}”." : $"Reopened “{poll.Title}”.");
    }

    public async Task<AdminOutcome> DeletePollAsync(ClaimsPrincipal caller, string encodedId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        await using var tx = await db.Database.BeginWriteAsync(ct);
        var poll = await access.GetForAdminAsync(db, encodedId, tracking: false, ct);
        if (poll is null) return AdminOutcome.NotFound;
        await PollService.DeleteCascadeAsync(db, [poll.Id], ct);
        await LogAsync(caller, "poll.delete", "poll", encodedId, poll.Title);
        await tx.CommitAsync(ct);
        notifier.End(poll.Id);
        return new AdminOutcome(true, $"Deleted “{poll.Title}”.");
    }

    /// <summary>
    /// Hands a poll to another account (DESIGN.md §14.9): an account that may act (not deleted, pending or gated) with a
    /// verified email. Ballots, invites and claims stay as they are; the new owner sees it under My polls. The
    /// old owner keeps his own ballots as a voter, but "Email me when this poll closes" is cleared on them (he may lose
    /// the results, e.g. owner-only ones; the sweep re-checks access anyway). The close date stays; the new owner's
    /// duration limit applies only when he changes it.
    /// </summary>
    public async Task<AdminOutcome> TransferPollAsync(ClaimsPrincipal caller, string encodedId, string? email, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        var poll = await access.GetForAdminAsync(db, encodedId, tracking: true, ct);
        if (poll is null) return AdminOutcome.NotFound;
        // Found through the address's keyed hash (DESIGN.md §30), like every lookup by email.
        var target = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim());
        if (target is null || emails.Reveal(target.Email) is not { } targetEmail || !await AccountGate.MayActAsync(db, target.Id, ct, verifiedEmail: true))
            return new AdminOutcome(false, "Enter the email of an account with a verified address.");
        if (target.Id == poll.OwnerId) return new AdminOutcome(false, $"{targetEmail} already owns this poll.");
        var oldOwner = poll.OwnerId;
        var from = emails.Display(await db.Users.Where(u => u.Id == oldOwner).Select(u => u.Email).SingleOrDefaultAsync(ct));
        await using var tx = await db.Database.BeginWriteAsync(ct);
        poll.OwnerId = target.Id;
        poll.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await db.Ballots.Where(b => b.PollId == poll.Id && b.UserId == oldOwner && b.NotifyOnClose)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.NotifyOnClose, false), ct);
        await tx.CommitAsync(ct);
        notifier.End(poll.Id);
        await LogAsync(caller, "poll.transfer", "poll", encodedId, $"{poll.Title}: {from ?? "(no email)"} -> {targetEmail}");
        return new AdminOutcome(true, $"“{poll.Title}” now belongs to {targetEmail}.");
    }

    // ---- users ----------------------------------------------------------------------------------

    /// <summary>Rows the Users tab shows at most; a search narrows the rest.</summary>
    public const int MaxUserRows = 200;

    /// <summary>Non-deleted accounts; pending ones first, then by email (all, without the row cap: tests and small callers).</summary>
    public async Task<IReadOnlyList<AdminUserRow>?> ListUsersAsync(ClaimsPrincipal caller, CancellationToken ct = default) =>
        (await SearchUsersAsync(caller, null, int.MaxValue, ct))?.Rows;

    /// <summary>
    /// Non-deleted accounts whose display name contains <paramref name="query"/> (case-insensitive) or whose email is exactly
    /// <paramref name="query"/> (any case; matched through its keyed hash, DESIGN.md §30: addresses are stored encrypted, so
    /// there is no substring search on them); empty = all. Pending ones first, then by email; at most
    /// <paramref name="max"/> rows plus the total match count.
    /// </summary>
    public async Task<AdminUserList?> SearchUsersAsync(ClaimsPrincipal caller, string? query, int max = MaxUserRows, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return null;
        var filtered = db.Users.AsNoTracking().Where(u => u.DeletedAt == null);
        if (query?.Trim() is { Length: > 0 } q)
        {
            if (q.Length > 256) q = q[..256];
            // SQLite's upper() folds ASCII only, so the display name is matched both as typed and upper-cased.
            var upper = q.ToUpperInvariant();
            var emailHash = emails.Hash(q);
            filtered = filtered.Where(u => u.NormalizedEmail == emailHash
                                           || (u.DisplayName != null && (u.DisplayName.Contains(q) || u.DisplayName.ToUpper().Contains(upper))));
        }
        var total = await filtered.CountAsync(ct);
        // Which rows make the cap is decided without the addresses (encrypted); the page is sorted by them below.
        var rows = await filtered
            .OrderByDescending(u => u.PendingSetup).ThenBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Take(max)
            .Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.PendingSetup, u.EmailConfirmed, u.TwoFactorEnabled, u.MustEnrolTwoFactor, u.LockoutEnd,
                u.AccessFailedCount, u.CreatedAt, u.LastLoginAt, u.LimitActivePolls, u.LimitPollsPerDay, u.LimitBallotsPerPoll,
                u.LimitInvitesPerPoll, u.LimitPollDurationDays, u.LimitResponsesPerVoter,
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var roles = await (from ur in db.UserRoles
                           where ids.Contains(ur.UserId)
                           join r in db.Roles on ur.RoleId equals r.Id
                           select new { ur.UserId, r.Name }).ToListAsync(ct);
        var rolesByUser = roles.ToLookup(x => x.UserId, x => x.Name ?? "");
        var pollCounts = await db.Polls.Where(p => ids.Contains(p.OwnerId)).GroupBy(p => p.OwnerId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        // Live setup links of pending accounts: expiry and issuing admin.
        var pendingIds = rows.Where(r => r.PendingSetup).Select(r => r.Id).ToList();
        var links = (await (from l in db.SetupLinks.AsNoTracking()
                            where pendingIds.Contains(l.UserId) && l.UsedAt == null && l.Purpose != SetupLinkPurpose.PasswordReset
                            join a in db.Users on l.CreatedBy equals a.Id into admins
                            from a in admins.DefaultIfEmpty()
                            select new { l.UserId, l.ExpiresAt, l.CreatedAt, By = a != null ? a.Email : null }).ToListAsync(ct))
            .Select(l => l with { By = emails.Display(l.By) })
            .GroupBy(l => l.UserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.CreatedAt).First());
        var list = rows.Select(u => new AdminUserRow(u.Id, emails.Display(u.Email), u.PendingSetup, u.EmailConfirmed, u.TwoFactorEnabled, u.MustEnrolTwoFactor,
                u.LockoutEnd, u.AccessFailedCount, rolesByUser[u.Id].OrderBy(n => n).ToList(), pollCounts.GetValueOrDefault(u.Id), u.CreatedAt, u.LastLoginAt)
        {
            PendingLinkExpiresAt = links.TryGetValue(u.Id, out var link) ? link.ExpiresAt : null,
            PendingLinkBy = links.TryGetValue(u.Id, out var by) ? by.By : null,
            Limits = new UserLimitOverrides(u.LimitActivePolls, u.LimitPollsPerDay, u.LimitBallotsPerPoll, u.LimitInvitesPerPoll,
                u.LimitPollDurationDays, u.LimitResponsesPerVoter),
            DisplayName = u.DisplayName,
        })
            .OrderByDescending(u => u.PendingSetup).ThenBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AdminUserList(list, total);
    }

    private async Task<(AdminOutcome? Error, ApplicationUser? User)> LoadUserAsync(ClaimsPrincipal caller, string userId)
    {
        if (!await IsAdminAsync(caller)) return (AdminOutcome.Denied, null);
        var user = await users.FindByIdAsync(userId);
        return user is null || user.DeletedAt is not null ? (AdminOutcome.NotFound, null) : (null, user);
    }

    private string Name(ApplicationUser u) => emails.Display(u.Email) ?? "pending account";

    /// <summary>
    /// Disables 2FA, removes the authenticator key, recovery codes and passkeys (pentest AV-06), signs the user out
    /// everywhere and makes them enrol again at their next sign-in.
    /// </summary>
    public async Task<AdminOutcome> DisableTwoFactorAsync(ClaimsPrincipal caller, string userId)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        user!.MustEnrolTwoFactor = true;
        var steps = new[]
        {
            await users.SetTwoFactorEnabledAsync(user, false),
            await users.RemoveAuthenticationTokenAsync(user, IdentityTokenProvider, "AuthenticatorKey"),
            await users.RemoveAuthenticationTokenAsync(user, IdentityTokenProvider, "RecoveryCodes"),
        };
        var passkeys = await accounts.RemovePasskeysAsync(user);
        steps = [.. steps, await users.UpdateSecurityStampAsync(user)];
        var failed = steps.FirstOrDefault(r => !r.Succeeded);
        if (failed is not null) return new AdminOutcome(false, Errors(failed));
        await LogAsync(caller, "user.disable-2fa", "user", user.Id, emails.Display(user.Email));
        // The account learns of it (pentest R2 Info f): an admin taking over another account does not go unnoticed. An
        // admin-issued reset link is mailed to the account already (AccountService.IssueAdminResetAsync).
        if (users.GetUserId(caller) != user.Id && user.EmailConfirmed && emails.Reveal(user.Email) is { } to)
        {
            await mail.EnqueueAsync(to, $"Two-factor authentication turned off on your {settings.AppName} account",
                $"An administrator turned off two-factor authentication on your {settings.AppName} account and removed its passkeys " +
                $"({time.GetUtcNow().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC). You set it up again at your next sign-in.\n\n" +
                "If you did not ask for this, contact the administrator.", MailType.SecurityNotice);
        }
        return new AdminOutcome(true, $"2FA disabled and {passkeys} {(passkeys == 1 ? "passkey" : "passkeys")} removed for {Name(user)}. New enrolment required at next sign-in.");
    }

    /// <summary>
    /// Admin lock (until unlocked). A locked admin cannot act (<see cref="IsAdminAsync"/>), so it runs with the
    /// last-admin check under the admin lock like demote and delete: two admins locking each other at once cannot both
    /// succeed (pentest R2-ACC-2).
    /// </summary>
    public Task<AdminOutcome> LockAsync(ClaimsPrincipal caller, string userId) =>
        accounts.SerializeAdminChangeAsync(() => LockLockedAsync(caller, userId));

    private async Task<AdminOutcome> LockLockedAsync(ClaimsPrincipal caller, string userId)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (users.GetUserId(caller) == user!.Id) return new AdminOutcome(false, "You cannot lock yourself.");
        if (await accounts.IsLastAdminAsync(user)) return new AdminOutcome(false, $"{Name(user)} is the last admin and cannot be locked.");
        await users.SetLockoutEnabledAsync(user, true);
        var result = await users.SetLockoutEndDateAsync(user, AccountGate.AdminLockEnd);
        // New security stamp: existing sessions are rejected at the next validation.
        await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) return new AdminOutcome(false, Errors(result));
        await LogAsync(caller, "user.lock", "user", user.Id, emails.Display(user.Email));
        return new AdminOutcome(true, $"Locked {Name(user)}.");
    }

    public async Task<AdminOutcome> UnlockAsync(ClaimsPrincipal caller, string userId)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (users.GetUserId(caller) == user!.Id) return new AdminOutcome(false, "Ask another admin to unlock you.");
        var result = await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        if (!result.Succeeded) return new AdminOutcome(false, Errors(result));
        await LogAsync(caller, "user.unlock", "user", user.Id, emails.Display(user.Email));
        return new AdminOutcome(true, $"Unlocked {Name(user)}.");
    }

    public Task<AdminOutcome> SetAdminAsync(ClaimsPrincipal caller, string userId, bool admin) =>
        accounts.SerializeAdminChangeAsync(() => SetAdminLockedAsync(caller, userId, admin));

    private async Task<AdminOutcome> SetAdminLockedAsync(ClaimsPrincipal caller, string userId, bool admin)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (!admin && await accounts.IsLastAdminAsync(user!)) return new AdminOutcome(false, $"{Name(user!)} is the last admin. Make another admin first.");
        if (!admin && users.GetUserId(caller) == user!.Id) return new AdminOutcome(false, "Ask another admin to remove your admin role.");
        // Only an account that may act with a verified email (not pending, gated or deleted), as for a transfer target.
        if (admin && !await AccountGate.MayActAsync(db, user!.Id, verifiedEmail: true))
            return new AdminOutcome(false, $"{Name(user)} needs a verified email and finished setup first.");
        // Admins must have 2FA (pentest AV-07): a promoted account without it is gated to enrolment first. The flag is
        // set before the stamp bump below, which saves it, so no session holds the role without the gate.
        var mustEnrol = admin && !await users.GetTwoFactorEnabledAsync(user!);
        if (mustEnrol) user!.MustEnrolTwoFactor = true;
        await using var tx = await db.Database.BeginWriteAsync();
        var result = admin ? await users.AddToRoleAsync(user!, AppRoles.Admin) : await users.RemoveFromRoleAsync(user!, AppRoles.Admin);
        if (!result.Succeeded) return new AdminOutcome(false, Errors(result));
        // The role reaches their cookie on next sign-in or security-stamp revalidation.
        await users.UpdateSecurityStampAsync(user!);
        await tx.CommitAsync();
        await LogAsync(caller, admin ? "user.promote" : "user.demote", "user", user!.Id, emails.Display(user.Email));
        return new AdminOutcome(true, !admin ? $"{Name(user)} is no longer an admin."
            : mustEnrol ? $"{Name(user)} is now an admin and must set up 2FA before using the role." : $"{Name(user)} is now an admin.");
    }

    public async Task<AdminOutcome> MarkEmailVerifiedAsync(ClaimsPrincipal caller, string userId)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (user!.Email is null) return new AdminOutcome(false, "No email set.");
        user.EmailConfirmed = true;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) return new AdminOutcome(false, Errors(result));
        await LogAsync(caller, "user.verify-email", "user", user.Id, emails.Display(user.Email));
        return new AdminOutcome(true, $"Marked {Name(user)} verified.");
    }

    /// <summary>Issues a 24 h reset link (shown once; mailed too when mail is configured).</summary>
    public async Task<AdminOutcome> ResetPasswordAsync(ClaimsPrincipal caller, string userId, string requestBaseUri)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (user!.PendingSetup) return new AdminOutcome(false, "Pending accounts need a setup link instead.");
        var url = await accounts.IssueAdminResetAsync(user, users.GetUserId(caller)!, requestBaseUri);
        await LogAsync(caller, "user.reset-password", "user", user.Id, emails.Display(user.Email));
        return new AdminOutcome(true, $"Reset link for {Name(user)} (24 h, single use; removes their passkeys):", url);
    }

    /// <summary>Soft-deletes the account and all polls it owns. Refuses the last admin and yourself.</summary>
    public Task<AdminOutcome> DeleteUserAsync(ClaimsPrincipal caller, string userId, CancellationToken ct = default) =>
        accounts.SerializeAdminChangeAsync(() => DeleteUserLockedAsync(caller, userId, ct), ct);

    private async Task<AdminOutcome> DeleteUserLockedAsync(ClaimsPrincipal caller, string userId, CancellationToken ct)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (users.GetUserId(caller) == user!.Id) return new AdminOutcome(false, "Delete your own account from your account page.");
        if (await accounts.IsLastAdminAsync(user)) return new AdminOutcome(false, $"{Name(user)} is the last admin and cannot be deleted.");
        var label = Name(user);
        var polls = await accounts.SoftDeleteAsync(user, ct);
        await LogAsync(caller, "user.delete", "user", user.Id, $"{label}, {polls} polls");
        return new AdminOutcome(true, $"Deleted {label} and {polls} {(polls == 1 ? "poll" : "polls")}.");
    }

    /// <summary>Sets the account's limit overrides (null = global default, 0 = no limit).</summary>
    public async Task<AdminOutcome> SetUserLimitsAsync(ClaimsPrincipal caller, string userId, UserLimitOverrides limits)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (UserLimitOverrides.Fields.Any(f => limits.Get(f.Field) is < 0)) return new AdminOutcome(false, "Limits must be 0 or more.");
        user!.LimitActivePolls = limits.ActivePolls;
        user.LimitPollsPerDay = limits.PollsPerDay;
        user.LimitBallotsPerPoll = limits.BallotsPerPoll;
        user.LimitInvitesPerPoll = limits.InvitesPerPoll;
        user.LimitPollDurationDays = limits.PollDurationDays;
        user.LimitResponsesPerVoter = limits.ResponsesPerVoter;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) return new AdminOutcome(false, Errors(result));
        var note = string.Join(", ", UserLimitOverrides.Fields.Where(f => limits.Get(f.Field) is not null).Select(f => $"{f.Field}={limits.Get(f.Field)}"));
        await LogAsync(caller, "user.limits", "user", user.Id, note.Length == 0 ? "defaults" : note);
        return new AdminOutcome(true, $"Limits saved for {Name(user)}.");
    }

    // ---- abuse blocks (in memory, DESIGN.md §14.7) ------------------------------------------------

    public async Task<IReadOnlyList<AbuseBlock>?> ListBlocksAsync(ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return null;
        var blocks = abuse.List();
        var accountIds = blocks.Where(b => b.Kind == AbuseKind.Account).Select(b => b.Key).ToList();
        var labels = await db.Users.AsNoTracking().Where(u => accountIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        return blocks.Select(b => b.Kind == AbuseKind.Account ? b with { Label = emails.Display(labels.GetValueOrDefault(b.Key)) ?? b.Label } : b).ToList();
    }

    public async Task<AdminOutcome> ClearBlockAsync(ClaimsPrincipal caller, string blockId)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        var block = abuse.List().FirstOrDefault(b => b.Id == blockId);
        if (block is null || !abuse.Clear(blockId)) return AdminOutcome.NotFound;
        // Never the key of an address block: client addresses stay out of the persistent log (pentest AV-08).
        await LogAsync(caller, "abuse.clear", "block", block.Kind == AbuseKind.Account ? block.Key : null,
            block.Kind == AbuseKind.Account ? (block.Manual ? "manual account block" : "automatic account block") : "automatic address block");
        return new AdminOutcome(true, "Block cleared.");
    }

    /// <summary>
    /// Manual block of an account (in memory, like the automatic blocks). Networks are blocked only through
    /// <see cref="AddRangeAsync"/> (one block mechanism for addresses: <see cref="BlockedRange"/>, with its safety checks).
    /// </summary>
    public async Task<AdminOutcome> AddBlockAsync(ClaimsPrincipal caller, string target, int minutes, string? note)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        target = target.Trim();
        if (minutes is < 1 or > 60 * 24 * 30) return new AdminOutcome(false, "Duration: 1 to 43200 minutes.");
        if (!target.Contains('@')) return new AdminOutcome(false, "Enter an account's email. Block addresses under Blocked ranges.");
        var user = await users.FindByEmailAsync(target);
        if (user is null || user.DeletedAt is not null) return new AdminOutcome(false, "No account with that email.");
        if (user.Id == users.GetUserId(caller)) return new AdminOutcome(false, "You cannot block yourself.");
        var block = abuse.AddAccount(user.Id, emails.Display(user.Email), TimeSpan.FromMinutes(minutes), note);
        await sessions.RevokeAllAsync(user.Id); // the block signs the account out everywhere (R3-DEP-2)
        await LogAsync(caller, "abuse.block", "block", null, $"{block.Label ?? block.Key}, {minutes} min{(note is { Length: > 0 } ? ", " + note : "")}");
        return new AdminOutcome(true, $"Blocked {block.Label ?? block.Key} for {minutes} min.");
    }

    // ---- persistent CIDR blocks (database, DESIGN.md §17) -------------------------------------------

    public async Task<IReadOnlyList<BlockedRangeRow>?> ListRangesAsync(ClaimsPrincipal caller, CancellationToken ct = default) =>
        await IsAdminAsync(caller) ? await ranges.ListAsync(ct) : null;

    /// <param name="days">Expiry in days from now; null = until removed.</param>
    public async Task<AdminOutcome> AddRangeAsync(ClaimsPrincipal caller, string? cidr, string? note, int? days)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        if (days is < 1 or > 3650) return new AdminOutcome(false, "Days: 1 to 3650, or empty for no expiry.");
        var (row, error) = await ranges.AddAsync(cidr, note, days is { } d ? TimeSpan.FromDays(d) : null, users.GetUserId(caller), actor: client.Address);
        if (row is null) return new AdminOutcome(false, error!);
        await LogAsync(caller, "abuse.range-add", "range", row.Cidr, $"{(days is { } n ? n + " days" : "no expiry")}{(row.Note is { } t ? ", " + t : "")}");
        return new AdminOutcome(true, $"Blocked {row.Cidr}.");
    }

    public async Task<AdminOutcome> RemoveRangeAsync(ClaimsPrincipal caller, Guid id)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        if (await ranges.RemoveAsync(id) is not { } cidr) return AdminOutcome.NotFound;
        await LogAsync(caller, "abuse.range-remove", "range", cidr);
        return new AdminOutcome(true, $"Removed {cidr}.");
    }

    // ---- branding images ({DataDir}/branding, DESIGN.md §17) -----------------------------------------

    public async Task<AdminOutcome> SaveBrandingAsync(ClaimsPrincipal caller, string name, Stream content, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        var result = await branding.SaveAsync(name, content, ct);
        if (!result.Ok) return new AdminOutcome(false, result.Message);
        await LogAsync(caller, "branding.save", "branding", name);
        return new AdminOutcome(true, result.Message);
    }

    public async Task<AdminOutcome> DeleteBrandingAsync(ClaimsPrincipal caller, string name)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        if (!branding.Delete(name)) return AdminOutcome.NotFound;
        await LogAsync(caller, "branding.remove", "branding", name);
        return new AdminOutcome(true, $"{BrandingService.Label(name)} removed.");
    }

    // ---- mail ---------------------------------------------------------------------------------------

    public async Task<AdminOutcome> PurgeDeadMailAsync(ClaimsPrincipal caller)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        var n = await mailSender.PurgeDeadAsync();
        await LogAsync(caller, "mail.purge-dead", note: $"{n} messages");
        return new AdminOutcome(true, n == 0 ? "No dead mail." : $"{n} dead {(n == 1 ? "message" : "messages")} deleted.");
    }

    // ---- account creation -------------------------------------------------------------------------

    /// <summary>Creates a pending account without email; returns the 7-day setup link to hand over.</summary>
    public async Task<AdminOutcome> CreateAccountAsync(ClaimsPrincipal caller, string requestBaseUri)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        var (result, user, token) = await accounts.CreatePendingAsync(null, users.GetUserId(caller)!);
        if (!result.Succeeded) return new AdminOutcome(false, Errors(result));
        await LogAsync(caller, "user.create", "user", user!.Id);
        return new AdminOutcome(true, "Setup link (7 days, single use):", AccountService.SetupUrl(accounts.LinkBase(requestBaseUri), token!));
    }

    /// <summary>Creates a pending account for <paramref name="email"/> and mails the setup link (Public URL based). Needs mail.</summary>
    public async Task<AdminOutcome> InviteAsync(ClaimsPrincipal caller, string email)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        if (!settings.Current.Mail.IsConfigured) return new AdminOutcome(false, "Configure mail first.");
        var (result, user, token) = await accounts.CreatePendingAsync(email, users.GetUserId(caller)!);
        if (!result.Succeeded) return new AdminOutcome(false, Errors(result));
        var invited = email.Trim();
        await accounts.SendInviteAsync(invited, token!);
        await LogAsync(caller, "user.invite", "user", user!.Id, invited);
        return new AdminOutcome(true, $"Invite queued for {invited}.");
    }

    /// <summary>New setup link for a pending account (the old one dies). Mailed again for invites.</summary>
    public async Task<AdminOutcome> RenewSetupLinkAsync(ClaimsPrincipal caller, string userId, string requestBaseUri)
    {
        var (error, user) = await LoadUserAsync(caller, userId);
        if (error is not null) return error;
        if (!user!.PendingSetup) return new AdminOutcome(false, "Account is already set up.");
        var purpose = user.Email is null ? SetupLinkPurpose.AccountSetup : SetupLinkPurpose.EmailInvite;
        var token = await accounts.IssueLinkAsync(user, purpose, AccountService.SetupLinkLifetime, users.GetUserId(caller));
        var url = AccountService.SetupUrl(accounts.LinkBase(requestBaseUri), token);
        if (emails.Reveal(user.Email) is { } invitee && settings.Current.Mail.IsConfigured) await accounts.SendInviteAsync(invitee, token);
        await LogAsync(caller, "user.setup-link", "user", user.Id, emails.Display(user.Email));
        return new AdminOutcome(true, "New setup link (7 days, single use):", url);
    }

    // ---- settings ---------------------------------------------------------------------------------

    /// <summary>Writes settings (keys from <see cref="SettingKeys"/>) and logs which keys changed (never values of secrets).</summary>
    public async Task<AdminOutcome> SaveSettingsAsync(ClaimsPrincipal caller, IReadOnlyDictionary<string, string?> values, string action)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        if (ThemeSettings.Validate(values) is { } invalid) return new AdminOutcome(false, invalid);
        if (LimitProblem(values) is { } limitProblem) return new AdminOutcome(false, limitProblem);
        if (values.TryGetValue(SettingKeys.PublicUrl, out var publicUrl) && SettingsService.PublicUrlProblem(publicUrl) is { } urlProblem)
            return new AdminOutcome(false, urlProblem);
        (var mailProblem, values) = BindMailCredential(values);
        if (mailProblem is not null) return new AdminOutcome(false, mailProblem);
        var before = settings.Current.Raw;
        var changed = values.Where(kv => (before.GetValueOrDefault(kv.Key) ?? "") != (kv.Value?.Trim() ?? "") || SettingKeys.Secrets.Contains(kv.Key))
            .Select(kv => kv.Key).ToList();
        await settings.SetAsync(values, users.GetUserId(caller));
        await LogAsync(caller, action, "settings", null, changed.Count == 0 ? "no changes" : string.Join(", ", changed));
        return new AdminOutcome(true, "Saved.");
    }

    /// <summary>
    /// The stored SMTP password is write-only (pentest AV-11): it may not follow the mail settings to another server or
    /// account. When host, port, security or username change and no new password comes with them, the stored one is
    /// cleared. A username with security None (password in clear text) is refused.
    /// </summary>
    private (string? Problem, IReadOnlyDictionary<string, string?> Values) BindMailCredential(IReadOnlyDictionary<string, string?> values)
    {
        var current = settings.Current.Raw;
        string Effective(IReadOnlyDictionary<string, string?> v, string key) =>
            (v.TryGetValue(key, out var value) ? value : current.GetValueOrDefault(key))?.Trim() ?? "";
        var username = Effective(values, SettingKeys.MailUsername);
        var security = Effective(values, SettingKeys.MailSecurity);
        if (username.Length > 0 && security.Equals(nameof(MailSecurity.None), StringComparison.OrdinalIgnoreCase))
            return ("Security None sends the password in clear text. Pick STARTTLS or implicit TLS, or remove the username.", values);

        // Compare what the transport would use (defaults applied), not the raw strings.
        var now = settings.Current.Mail;
        var port = int.TryParse(Effective(values, SettingKeys.MailPort), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : 587;
        var sec = Enum.TryParse<MailSecurity>(security, true, out var parsed) ? parsed : MailSecurity.StartTls;
        var moved = !string.Equals(Effective(values, SettingKeys.MailHost), now.Host ?? "", StringComparison.OrdinalIgnoreCase)
                    || port != now.Port || sec != now.Security
                    || !string.Equals(username, now.Username ?? "", StringComparison.Ordinal);
        var newPassword = values.TryGetValue(SettingKeys.MailPassword, out var p) && !string.IsNullOrEmpty(p);
        if (moved && !newPassword && current.ContainsKey(SettingKeys.MailPassword))
            return (null, new Dictionary<string, string?>(values) { [SettingKeys.MailPassword] = null });
        return (null, values);
    }

    /// <summary>Range checks for limits whose value has bounds beyond "0 or more" (voter names, the text and option ceilings).</summary>
    private static string? LimitProblem(IReadOnlyDictionary<string, string?> values)
    {
        static int? Value(IReadOnlyDictionary<string, string?> v, string key) =>
            v.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (Value(values, SettingKeys.LimitVoterName) is int name && name is < Voting.BallotService.MinNameLimit or > Voting.BallotService.MaxNameLimit)
            return $"Voter name length: {Voting.BallotService.MinNameLimit} to {Voting.BallotService.MaxNameLimit}.";
        foreach (var (key, max) in LimitSettings.Ceilings)
        {
            if (Value(values, key) is int n && n > max)
                return $"{LimitSettings.Fields.First(f => f.Key == key).Label}: at most {max}.";
        }
        return null;
    }

    public async Task<AdminOutcome> SendTestMailAsync(ClaimsPrincipal caller)
    {
        if (!await IsAdminAsync(caller)) return AdminOutcome.Denied;
        if (!settings.Current.Mail.IsConfigured) return new AdminOutcome(false, "Configure mail first.");
        var me = await users.GetUserAsync(caller);
        if (emails.Reveal(me?.Email) is not { } myEmail) return new AdminOutcome(false, "Your account has no email.");
        await mail.EnqueueAsync(myEmail, $"{settings.AppName} test mail",
            $"Test message from {settings.AppName}, sent {time.GetUtcNow().ToString("u", CultureInfo.InvariantCulture)}.", MailType.Test);
        await LogAsync(caller, "mail.test", "user", me!.Id, myEmail);
        return new AdminOutcome(true, $"Test mail queued for {myEmail}.");
    }

    public async Task<(IReadOnlyList<AdminLogRow> Rows, int Total)?> ListLogAsync(ClaimsPrincipal caller, int page, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(caller)) return null;
        return await AdminLog.PageAsync(db, emails, page, ct);
    }

    public async Task LogBackupAsync(ClaimsPrincipal caller) => await LogAsync(caller, "backup.download");

    private static string Errors(IdentityResult r) => string.Join(" ", r.Errors.Select(e => e.Description));
}
