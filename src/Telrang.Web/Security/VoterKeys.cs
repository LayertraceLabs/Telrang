using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Telrang.Web.Security;

/// <summary>
/// Turns the raw link-mode <c>vk</c> cookie value into the per-poll <see cref="Data.Ballot.VoterKey"/>:
/// base64url(HMAC-SHA256(secret, vk || pollId)). The same browser therefore has unrelated voter keys
/// in different polls, so the database alone cannot link one person's ballots across polls.
/// <para>The 32-byte secret is generated once and stored in <c>{DataDir}/keys/voterkey</c>, encrypted
/// with Data Protection (so it is as safe, and as persistent, as the key ring next to it). Losing
/// it (or the key ring) means link-mode voters can no longer revise ballots cast before.</para>
/// </summary>
public sealed class VoterKeys
{
    private const string Purpose = "Telrang.VoterKeySecret.v1";
    private readonly Lazy<byte[]> secret;

    public VoterKeys(IDataProtectionProvider dataProtection, string secretFile)
    {
        var protector = dataProtection.CreateProtector(Purpose);
        secret = new Lazy<byte[]>(() => LoadOrCreate(protector, secretFile), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The ballot VoterKey for raw cookie key <paramref name="vk"/> in poll <paramref name="pollId"/>.</summary>
    public string ForPoll(string vk, Guid pollId)
    {
        var vkBytes = Encoding.UTF8.GetBytes(vk);
        var input = new byte[vkBytes.Length + 16];
        vkBytes.CopyTo(input, 0);
        pollId.TryWriteBytes(input.AsSpan(vkBytes.Length));
        return Base64Url.EncodeToString(HMACSHA256.HashData(secret.Value, input));
    }

    private static byte[] LoadOrCreate(IDataProtector protector, string file) =>
        File.Exists(file) ? protector.Unprotect(File.ReadAllBytes(file)) : SecretFiles.Create(protector, file);
}
