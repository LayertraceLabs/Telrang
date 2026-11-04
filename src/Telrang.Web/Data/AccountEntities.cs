namespace Telrang.Web.Data;

/// <summary>One DB-backed setting (see Features/Settings/SettingKeys). Secrets are Data-Protection protected.</summary>
public class AppSetting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public enum SetupLinkPurpose
{
    /// <summary>Admin created the account without an email; the link sets email, password, TOTP.</summary>
    AccountSetup,
    /// <summary>Single-use password reset (self-service 30 min, admin-issued 24 h).</summary>
    PasswordReset,
    /// <summary>Like AccountSetup, but mailed to a known address (which the link therefore verifies).</summary>
    EmailInvite,
}

/// <summary>Single-use link for account setup or password reset. Only the SHA-256 of the token is stored.</summary>
public class SetupLink
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>base64url(SHA-256(32 random bytes)).</summary>
    public required string TokenHash { get; set; }
    public SetupLinkPurpose Purpose { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Admin user id, or null for self-service.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>User's security stamp when issued (password reset): any password change invalidates the link.</summary>
    public string? SecurityStamp { get; set; }
}

public enum MailType
{
    Verification,
    EmailChange,
    EmailChangeNotice,
    PasswordReset,
    AccountInvite,
    Test,
    /// <summary>"This poll closed" to a voter who ticked "Email me when this poll closes" (opt-in, verified address).</summary>
    ResultsReady,
    /// <summary>"A passkey was added" / "new recovery codes" to the account's verified address (pentest AV-02).</summary>
    SecurityNotice,
    /// <summary>"Someone tried to register with your address" to an existing account (pentest AV-10).</summary>
    RegistrationAttempt,
}

/// <summary>
/// Outbox row. The background sender delivers it; nothing sends mail directly. The bodies (which can hold a live setup or
/// reset link) are encrypted with the key ring while queued (pentest R3-DEP-6) and cleared once the message is sent or
/// dead (pentest AV-04); sent rows are deleted after <see cref="Features.Mail.MailSender.SentRetention"/>. The rest stays
/// for the cooldown, the caps and the Mail tab. The recipient is encrypted, its keyed hash counts for the per-recipient
/// limits (DESIGN.md §30, <see cref="Security.EmailProtection"/>).
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>The recipient, encrypted (<see cref="Security.EmailProtection.Protect"/>).</summary>
    public required string To { get; set; }

    /// <summary>The recipient's lookup hash (<see cref="Security.EmailProtection.Hash"/>): the per-recipient cooldown and cap.</summary>
    public string? ToHash { get; set; }
    public required string Subject { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public MailType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset SendAfter { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeadAt { get; set; }
}

/// <summary>
/// A signed-in session (DESIGN.md §31, pentest R3-DEP-2). The authentication cookie holds only a random 32-byte key; the
/// row is found by its SHA-256 (<see cref="Id"/>), so a database copy holds no usable key, and the ticket is encrypted with
/// the key ring, so the database alone does not show it either. A key ring copy alone signs nobody in: the row must exist.
/// Deleted on sign-out, "log out everywhere", any security-stamp change (password, 2FA, lock, delete) and account blocks.
/// </summary>
public class AuthSession
{
    /// <summary>base64url(SHA-256(the cookie's key)).</summary>
    public required string Id { get; set; }
    public required string UserId { get; set; }

    /// <summary>The authentication ticket, serialised and protected with Data Protection (base64url).</summary>
    public required string Ticket { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Who did what as an admin. Rotated at 10 000 rows (oldest first).</summary>
public class AdminActionLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string AdminUserId { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Note { get; set; }
}
