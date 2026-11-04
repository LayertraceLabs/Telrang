using System.Security.Cryptography;
using Telrang.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Security;

/// <summary>
/// A hash of a random throw-away password, computed once at startup with the real
/// <see cref="PasswordHasherOptions"/> (600k PBKDF2 iterations). The login page verifies against it
/// when the email is unknown, so unknown and known accounts take about the same time to fail.
/// </summary>
public sealed class DummyPasswordHash
{
    private static readonly ApplicationUser Nobody = new();
    private readonly PasswordHasher<ApplicationUser> hasher;
    private readonly string hash;

    public DummyPasswordHash(IOptions<PasswordHasherOptions> options)
    {
        hasher = new PasswordHasher<ApplicationUser>(options);
        hash = hasher.HashPassword(Nobody, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
    }

    /// <summary>Burns the same CPU as a real verification; the result is always "failed".</summary>
    public void Verify(string? password) => hasher.VerifyHashedPassword(Nobody, hash, password ?? "");
}
