using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telrang.Web.Data;

namespace Telrang.Web.Security;

/// <summary>
/// Server-side sessions for the Identity application cookie (DESIGN.md §31, pentest R3-DEP-2). The cookie carries only a
/// random key; the ticket lives in <see cref="AuthSession"/>, encrypted with the key ring. Every request looks the row up
/// and compares the ticket's security stamp with the account's, so a key ring copy alone (which could forge any cookie)
/// signs nobody in, and a session ends at once when it is removed: sign-out, "log out everywhere", a security-stamp change
/// (password, 2FA, admin lock, delete; <see cref="ProtectedTokenUserStore"/> deletes the rows) or an account block
/// (<see cref="RevokeAllAsync"/>).
/// </summary>
public sealed class AuthSessionStore(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    IOptions<IdentityOptions> identityOptions,
    TimeProvider time) : ITicketStore
{
    public const string Purpose = "Telrang.AuthSession.v1";

    /// <summary>A session without an expiry in its ticket ends after this (the cookie's own lifetime).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(14);

    private readonly IDataProtector protector = dataProtection.CreateProtector(Purpose);

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        StoreAsync(ticket, null!, cancellationToken);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var (key, hash) = SecretTokens.Generate();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuthSessions.Add(NewRow(hash, ticket));
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        RenewAsync(key, ticket, null!, cancellationToken);

    /// <summary>
    /// Sliding expiration: the ticket of a live session gets a later expiry. A session removed in the meantime (sign-out,
    /// revocation) is not brought back. Sign-ins never come here: <see cref="SessionCookieHandler"/> gives every sign-in a
    /// new key.
    /// </summary>
    public async Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!SecretTokens.TryHash(key, out var hash)) return;
        var row = NewRow(hash, ticket);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.AuthSessions.Where(s => s.Id == hash && s.UserId == row.UserId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Ticket, row.Ticket).SetProperty(x => x.ExpiresAt, row.ExpiresAt), cancellationToken);
    }

    private AuthSession NewRow(string hash, AuthenticationTicket ticket) => new()
    {
        Id = hash,
        UserId = ticket.Principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType)
                 ?? throw new InvalidOperationException("A session needs a user id."),
        Ticket = Protect(ticket),
        CreatedAt = time.GetUtcNow(),
        ExpiresAt = ExpiresAt(ticket),
    };

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken) =>
        RetrieveAsync(key, null!, cancellationToken);

    /// <summary>
    /// The session's ticket, or null (the cookie then signs nobody in): unknown or malformed key, expired, unreadable
    /// (key ring changed), deleted account, or a security stamp that changed since sign-in. Stale rows are deleted.
    /// </summary>
    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!SecretTokens.TryHash(key, out var hash)) return null;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await (from s in db.AuthSessions.AsNoTracking()
                         where s.Id == hash
                         join u in db.Users on s.UserId equals u.Id
                         select new { s.Ticket, s.ExpiresAt, u.SecurityStamp, u.DeletedAt }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var ticket = row.ExpiresAt > time.GetUtcNow() && row.DeletedAt is null ? Unprotect(row.Ticket) : null;
        var stamp = ticket?.Principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        if (ticket is null || stamp is null || !string.Equals(stamp, row.SecurityStamp, StringComparison.Ordinal))
        {
            await db.AuthSessions.Where(s => s.Id == hash).ExecuteDeleteAsync(cancellationToken);
            return null;
        }
        return ticket;
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public Task RemoveAsync(string key, CancellationToken cancellationToken) => RemoveAsync(key, null!, cancellationToken);

    public async Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!SecretTokens.TryHash(key, out var hash)) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.AuthSessions.Where(s => s.Id == hash).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Ends every session of an account now (account blocks; stamp changes go through the user store).</summary>
    public async Task<int> RevokeAllAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuthSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Deletes expired sessions (the sweep's daily pass). Returns how many.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuthSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);
    }

    private DateTimeOffset ExpiresAt(AuthenticationTicket ticket) => ticket.Properties.ExpiresUtc ?? time.GetUtcNow() + DefaultLifetime;

    private string Protect(AuthenticationTicket ticket) =>
        Base64Url.EncodeToString(protector.Protect(TicketSerializer.Default.Serialize(ticket)));

    private AuthenticationTicket? Unprotect(string value)
    {
        try { return TicketSerializer.Default.Deserialize(protector.Unprotect(Base64Url.DecodeFromChars(value))); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}

/// <summary>
/// Puts the application cookie's tickets into <see cref="AuthSessionStore"/> and lets <see cref="SessionCookieHandler"/>
/// handle that scheme.
/// </summary>
public sealed class AuthSessionCookieSetup(AuthSessionStore store) : IPostConfigureOptions<CookieAuthenticationOptions>, IPostConfigureOptions<AuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (name == IdentityConstants.ApplicationScheme) options.SessionStore = store;
    }

    public void PostConfigure(string? name, AuthenticationOptions options)
    {
        if (options.SchemeMap.TryGetValue(IdentityConstants.ApplicationScheme, out var scheme)) scheme.HandlerType = typeof(SessionCookieHandler);
    }
}

/// <summary>
/// The cookie handler for the application scheme with one change: every sign-in (also a refresh after a password, email
/// or 2FA change) ends the request's session and starts one under a new key. The stock handler keeps the request's key
/// and renews its row, so a copy of the cookie taken before a password change would stay signed in.
/// </summary>
public sealed class SessionCookieHandler(IOptionsMonitor<CookieAuthenticationOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
    : CookieAuthenticationHandler(options, logger, encoder)
{
    // The stock handler's session key of the request (set while it reads the cookie; null starts a new session).
    private static readonly System.Reflection.FieldInfo SessionKey =
        typeof(CookieAuthenticationHandler).GetField("_sessionKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CookieAuthenticationHandler has no _sessionKey field: check SessionCookieHandler against this ASP.NET Core version.");

    protected override async Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
    {
        await HandleAuthenticateOnceSafeAsync(); // reads the request's cookie (cached), as the base does first
        if (SessionKey.GetValue(this) is string old && Options.SessionStore is { } store)
        {
            await store.RemoveAsync(old, Context, Context.RequestAborted);
            SessionKey.SetValue(this, null);
        }
        await base.HandleSignInAsync(user, properties);
    }
}
