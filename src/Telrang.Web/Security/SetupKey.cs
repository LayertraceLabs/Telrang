using System.Security.Cryptography;

namespace Telrang.Web.Security;

/// <summary>
/// The one-time admin setup key (DESIGN.md §14.1). While no admin exists, startup issues a key, logs
/// it once and keeps only its SHA-256 and expiry here. Never written to disk; a restart issues a new
/// one. <see cref="Check"/> is constant-time; the caller revokes the key once the admin exists.
/// </summary>
public class SetupKey(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly Lock gate = new();
    private byte[]? hash;
    private DateTimeOffset expiresAt;

    public bool IsLive
    {
        get { lock (gate) return hash is not null && time.GetUtcNow() < expiresAt; }
    }

    /// <summary>Issues a fresh key (replacing any previous one) and returns it. Callers log it; tests read it.</summary>
    public string Issue()
    {
        var key = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            hash = Hash(key);
            expiresAt = time.GetUtcNow() + Lifetime;
        }
        return key;
    }

    /// <summary>Constant-time check without consuming.</summary>
    public bool Check(string? key)
    {
        byte[]? expected;
        lock (gate)
        {
            if (hash is null || time.GetUtcNow() >= expiresAt) return false;
            expected = hash;
        }
        return CryptographicOperations.FixedTimeEquals(Hash((key ?? "").Trim()), expected);
    }

    private static byte[] Hash(string key) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));

    /// <summary>Kills the key (an admin now exists).</summary>
    public void Revoke()
    {
        lock (gate) hash = null;
    }
}

/// <summary>
/// One-time admin recovery key: issued at startup only while the operator sets
/// <c>App__RecoveryKey=1</c> (or <c>App:IssueRecoveryKey=true</c>), even when admins exist. Opens
/// <c>/setup/recover</c> for one hour: pick an admin, set a new password, clear their 2FA.
/// </summary>
public sealed class RecoveryKey(TimeProvider time) : SetupKey(time)
{
    public static bool Requested(IConfiguration config) =>
        config.GetValue<bool>("App:IssueRecoveryKey") || config["App:RecoveryKey"] is "1" or "true" or "True";
}
