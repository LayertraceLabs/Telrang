using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Telrang.Web.Security;

/// <summary>
/// Email addresses at rest (DESIGN.md §30): a database copy must not hand over the list of users' addresses.
/// <list type="bullet">
/// <item>Readable values (<c>Email</c>, <c>UserName</c>, outbox recipients, admin log notes) are encrypted with Data
/// Protection, purpose <see cref="Purpose"/>, stored as <see cref="Prefix"/> + ciphertext, and decrypted only where the app
/// shows or sends them (<see cref="Reveal"/>).</item>
/// <item>Lookup values (<c>NormalizedEmail</c>, <c>NormalizedUserName</c>, the outbox's <c>ToHash</c>) are a keyed hash of
/// the normalised address: base64url(HMAC-SHA256(secret, upper-invariant address)). The 32-byte secret is
/// <c>{DataDir}/keys/emailkey</c>, protected with the key ring like <c>voterkey</c>; it does not rotate with the ring.</item>
/// </list>
/// A value without the prefix was written before §30 (or is a pending account's id): it is read as it is and encrypted by
/// the startup upgrade (<see cref="Data.EmailUpgrade"/>) or on its next save. A value that cannot be decrypted (key ring
/// lost) reads as null; the first such failure is logged once.
/// </summary>
public sealed partial class EmailProtection
{
    public const string Purpose = "Telrang.Email.v1";
    public const string Prefix = "e1:";
    private const string SecretPurpose = "Telrang.EmailKeySecret.v1";

    /// <summary>What the admin pages show for an address that cannot be decrypted.</summary>
    public const string UnreadableLabel = "unreadable address";

    private readonly IDataProtector protector;
    private readonly IDataProtector bodyProtector;
    private readonly Lazy<byte[]> secret;
    private readonly ILogger<EmailProtection> logger;
    private int unreadableLogged;

    public EmailProtection(IDataProtectionProvider dataProtection, string secretFile, ILogger<EmailProtection> logger)
    {
        protector = dataProtection.CreateProtector(Purpose);
        bodyProtector = dataProtection.CreateProtector(BodyPurpose);
        var secretProtector = dataProtection.CreateProtector(SecretPurpose);
        secret = new Lazy<byte[]>(() => LoadSecret(secretProtector, secretFile), LazyThreadSafetyMode.ExecutionAndPublication);
        this.logger = logger;
    }

    /// <summary>The value to store for a readable address (or a note that may hold one).</summary>
    public string Protect(string plain) => Prefix + protector.Protect(plain);

    /// <summary>The stored value is encrypted (has the prefix).</summary>
    public static bool IsProtected(string? stored) => stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The address (or note) for a stored value: null for null and for a value that cannot be decrypted; a value without the
    /// prefix (written before §30) as it is.
    /// </summary>
    public string? Reveal(string? stored)
    {
        if (stored is null || !IsProtected(stored)) return stored;
        try
        {
            return protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (CryptographicException)
        {
            if (Interlocked.Exchange(ref unreadableLogged, 1) == 0)
                logger.LogError("Stored email addresses cannot be decrypted: the Data Protection key ring ({{DataDir}}/keys) is not the one they were written with. Those accounts show \"{Label}\" and cannot sign in by email. Restore the key directory from a backup.", UnreadableLabel);
            return null;
        }
    }

    /// <summary>A stored value that is encrypted but cannot be decrypted with this key ring (checked without logging).</summary>
    public bool IsUnreadable(string? stored)
    {
        if (!IsProtected(stored)) return false;
        try { protector.Unprotect(stored![Prefix.Length..]); return false; }
        catch (CryptographicException) { return true; }
    }

    /// <summary>The startup check has logged the unreadable addresses: <see cref="Reveal"/> does not log them again.</summary>
    public void UnreadableReported() => Interlocked.Exchange(ref unreadableLogged, 1);

    /// <summary><see cref="Reveal"/> for display: the address, <see cref="UnreadableLabel"/>, or null when none is stored.</summary>
    public string? Display(string? stored) => stored is null ? null : Reveal(stored) ?? UnreadableLabel;

    // ---- outbox bodies (pentest R3-DEP-6) ------------------------------------------------------------

    /// <summary>Queued mail bodies (they can hold a live reset, invite or verification link) until they are sent.</summary>
    public const string BodyPurpose = "Telrang.OutboxBody.v1";
    public const string BodyPrefix = "b1:";

    public string ProtectBody(string body) => BodyPrefix + bodyProtector.Protect(body);

    public static bool IsProtectedBody(string? stored) => stored?.StartsWith(BodyPrefix, StringComparison.Ordinal) == true;

    /// <summary>A queued body to send: null when it cannot be decrypted (key ring lost); one without the prefix as it is.</summary>
    public string? RevealBody(string? stored)
    {
        if (stored is null || !IsProtectedBody(stored)) return stored;
        try { return bodyProtector.Unprotect(stored[BodyPrefix.Length..]); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>The lookup hash of an address as typed (normalised like Identity's: Unicode NFC, upper invariant).</summary>
    public string Hash(string address) => HashNormalized(Normalize(address));

    /// <summary>The lookup hash of an already normalised address (what Identity passes to the user store).</summary>
    public string HashNormalized(string normalized) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(secret.Value, Encoding.UTF8.GetBytes(normalized)));

    /// <summary>Identity's <c>UpperInvariantLookupNormalizer</c>: the same key for the same address in any case.</summary>
    public static string Normalize(string address) => address.Normalize().ToUpperInvariant();

    /// <summary>Error text from a mail server with every address in it replaced (it is stored and logged).</summary>
    public static string WithoutAddresses(string text) => AddressPattern().Replace(text, "<address>");

    [GeneratedRegex(@"<?[^\s<>""'(),;:]+@[^\s<>""'(),;:]+>?")]
    private static partial Regex AddressPattern();

    /// <summary>
    /// The hash secret: read from <paramref name="file"/>, or created there. A file the key ring cannot decrypt (the ring was
    /// lost) is moved aside and a new secret is made: lookups of the addresses hashed with the old one then fail (those
    /// accounts cannot sign in by email), new ones work.
    /// </summary>
    private byte[] LoadSecret(IDataProtector secretProtector, string file)
    {
        if (File.Exists(file))
        {
            try
            {
                return secretProtector.Unprotect(File.ReadAllBytes(file));
            }
            catch (CryptographicException)
            {
                var aside = file + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(file, aside);
                logger.LogError("The email lookup key {File} cannot be decrypted with this key ring; moved it to {Aside} and made a new one. Accounts created before cannot sign in by email until the key directory is restored.", file, aside);
            }
        }
        return SecretFiles.Create(secretProtector, file);
    }
}

/// <summary>32-byte secrets kept in the key directory, encrypted with the key ring (<c>voterkey</c>, <c>emailkey</c>).</summary>
public static class SecretFiles
{
    /// <summary>Writes a new secret to <paramref name="file"/> (atomically; another process that won the race wins).</summary>
    public static byte[] Create(IDataProtector protector, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var key = RandomNumberGenerator.GetBytes(32);
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(key));
        try
        {
            File.Move(tmp, file, overwrite: false);
            return key;
        }
        catch (IOException) when (File.Exists(file))
        {
            File.Delete(tmp);
            return protector.Unprotect(File.ReadAllBytes(file));
        }
    }
}
