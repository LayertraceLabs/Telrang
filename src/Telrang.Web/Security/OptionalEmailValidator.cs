using System.ComponentModel.DataAnnotations;
using Telrang.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace Telrang.Web.Security;

/// <summary>
/// Pending and deleted accounts have no email, which Identity's RequireUniqueEmail forbids. That
/// option is off; this validator enforces a valid, unique email whenever one is set.
/// </summary>
public sealed class OptionalEmailValidator : IUserValidator<ApplicationUser>
{
    private static readonly EmailAddressAttribute EmailFormat = new();

    public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        var email = await manager.GetEmailAsync(user);
        if (string.IsNullOrEmpty(email)) return IdentityResult.Success;
        if (!EmailFormat.IsValid(email) || email.Length > 256)
            return IdentityResult.Failed(manager.ErrorDescriber.InvalidEmail(email));
        var owner = await manager.FindByEmailAsync(email);
        if (owner is not null && !string.Equals(await manager.GetUserIdAsync(owner), await manager.GetUserIdAsync(user), StringComparison.Ordinal))
            return IdentityResult.Failed(manager.ErrorDescriber.DuplicateEmail(email));
        return IdentityResult.Success;
    }
}
