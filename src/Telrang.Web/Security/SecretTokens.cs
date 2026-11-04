using System.Buffers.Text;
using System.Security.Cryptography;

namespace Telrang.Web.Security;

/// <summary>
/// 32-byte random tokens as 43-char base64url (setup links, password reset). Only
/// base64url(SHA-256(bytes)) is stored; a plain hash suffices for 256 bits of entropy.
/// </summary>
public static class SecretTokens
{
    public const int ByteLength = 32;
    public const int TokenLength = 43;

    public static (string Token, string Hash) Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(ByteLength);
        return (Base64Url.EncodeToString(bytes), Hash(bytes));
    }

    /// <summary>False for anything that is not exactly a canonical 43-char base64url token.</summary>
    public static bool TryHash(string? token, out string hash)
    {
        hash = "";
        if (!TryDecode(token, out var bytes)) return false;
        hash = Hash(bytes);
        return true;
    }

    public static bool TryDecode(string? token, out byte[] bytes)
    {
        bytes = [];
        if (token is null || token.Length != TokenLength) return false;
        foreach (var c in token)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        }
        try { bytes = Base64Url.DecodeFromChars(token); }
        catch (FormatException) { return false; }
        return bytes.Length == ByteLength && Base64Url.EncodeToString(bytes) == token;
    }

    public static string Hash(byte[] bytes) => Base64Url.EncodeToString(SHA256.HashData(bytes));
}
