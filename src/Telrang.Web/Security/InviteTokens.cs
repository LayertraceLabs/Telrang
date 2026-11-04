using System.Buffers.Text;
using System.Security.Cryptography;

namespace Telrang.Web.Security;

/// <summary>
/// Invite tokens: 128 random bits as 22-char base64url. Only base64url(SHA-256(token bytes)) is
/// stored; a plain hash is enough because the input has 128 bits of entropy.
/// </summary>
public static class InviteTokens
{
    public const int TokenLength = 22;

    public static (string Token, string Hash) Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return (Base64Url.EncodeToString(bytes), HashBytes(bytes));
    }

    /// <summary>False for anything that is not exactly a canonical 22-char base64url token.</summary>
    public static bool TryHash(string? token, out string hash)
    {
        hash = "";
        if (token is null || token.Length != TokenLength) return false;
        foreach (var c in token)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        }
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(token); }
        catch (FormatException) { return false; }
        if (bytes.Length != 16 || Base64Url.EncodeToString(bytes) != token) return false;
        hash = HashBytes(bytes);
        return true;
    }

    private static string HashBytes(byte[] bytes) => Base64Url.EncodeToString(SHA256.HashData(bytes));
}
