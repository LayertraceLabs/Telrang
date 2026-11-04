using Telrang.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Security;

/// <summary>
/// Identity's EF user store with what a database copy must not reveal encrypted:
/// <list type="bullet">
/// <item>The values of Identity's own tokens (the authenticator key and the recovery codes, login provider
/// <c>[AspNetUserStore]</c>), with Data Protection (pentest AV-12): a database copy alone does not bypass anyone's 2FA.
/// Stored values carry the <see cref="Prefix"/>; a value without it (written before this store) is read as is and
/// protected on its next write.</item>
/// <item>Email addresses (DESIGN.md §30): <c>Email</c> and <c>UserName</c> hold the address encrypted
/// (<see cref="EmailProtection"/>), <c>NormalizedEmail</c> and <c>NormalizedUserName</c> its keyed hash. Identity reads
/// and writes them through this store only, so lookups (sign-in, registration, uniqueness, reset, email change, transfer)
/// work on the hash and Identity sees plain addresses. Code outside never reads <c>user.Email</c> directly: it calls
/// <see cref="UserManager{TUser}.GetEmailAsync"/> or <see cref="EmailProtection.Reveal"/>. A plain value assigned in code
/// is encrypted when the account is saved.</item>
/// </list>
/// An address that cannot be decrypted (key ring lost) reads as no email; the user name then reads as the account id, so
/// the account still saves.
/// </summary>
public sealed class ProtectedTokenUserStore(ApplicationDbContext context, IDataProtectionProvider dataProtection, EmailProtection emails,
    IdentityErrorDescriber? describer = null)
    : UserStore<ApplicationUser, IdentityRole, ApplicationDbContext>(context, describer)
{
    public const string Purpose = "Telrang.IdentityTokens.v1";
    public const string Prefix = "dp1:";
    private const string InternalProvider = "[AspNetUserStore]";

    private readonly IDataProtector protector = dataProtection.CreateProtector(Purpose);

    public override Task SetTokenAsync(ApplicationUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(user, loginProvider, name,
            loginProvider == InternalProvider && value is not null ? Prefix + protector.Protect(value) : value, cancellationToken);

    public override async Task<string?> GetTokenAsync(ApplicationUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        if (loginProvider != InternalProvider || value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        try { return protector.Unprotect(value[Prefix.Length..]); }
        catch (System.Security.Cryptography.CryptographicException) { return null; } // key ring lost: the secret is gone
    }

    // ---- email addresses (DESIGN.md §30) -------------------------------------------------------------

    public override Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.UserName is null ? null : emails.Reveal(user.UserName) ?? user.Id);
    }

    public override Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.UserName = userName is null ? null : emails.Protect(userName);
        return Task.CompletedTask;
    }

    public override Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.NormalizedUserName = normalizedName is null ? null : emails.HashNormalized(normalizedName);
        return Task.CompletedTask;
    }

    public override Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken = default) =>
        base.FindByNameAsync(emails.HashNormalized(normalizedUserName), cancellationToken);

    public override Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(emails.Reveal(user.Email));
    }

    public override Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.Email = email is null ? null : emails.Protect(email);
        return Task.CompletedTask;
    }

    public override Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.NormalizedEmail = normalizedEmail is null ? null : emails.HashNormalized(normalizedEmail);
        return Task.CompletedTask;
    }

    public override Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        base.FindByEmailAsync(emails.HashNormalized(normalizedEmail), cancellationToken);

    public override Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ProtectAddresses(user);
        return base.CreateAsync(user, cancellationToken);
    }

    /// <summary>
    /// Saves the account, addresses encrypted. When its security stamp changed (password, 2FA, email, "log out
    /// everywhere", admin lock, delete), every server-side session of it ends with the save (pentest R3-DEP-2); a sign-in
    /// refreshed in the same request starts a new one afterwards.
    /// </summary>
    public override async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ProtectAddresses(user);
        var storedStamp = await Context.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.SecurityStamp)
            .SingleOrDefaultAsync(cancellationToken);
        var result = await base.UpdateAsync(user, cancellationToken);
        if (result.Succeeded && !string.Equals(storedStamp, user.SecurityStamp, StringComparison.Ordinal))
            await Context.AuthSessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        return result;
    }

    /// <summary>A plain address assigned in code (<c>new ApplicationUser { Email = … }</c>) is encrypted before it is saved.</summary>
    private void ProtectAddresses(ApplicationUser user)
    {
        if (user.UserName is { } name && !EmailProtection.IsProtected(name)) user.UserName = emails.Protect(name);
        if (user.Email is { } email && !EmailProtection.IsProtected(email)) user.Email = emails.Protect(email);
    }
}
