using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Telrang.Web.Security;

/// <summary>
/// Free text and unfinished answers at rest (DESIGN.md §34.3, §34.4), encrypted like email addresses (§30): Data Protection
/// with a purpose of their own. The plaintext is compressed first (ciphertext does not compress): Brotli when that is
/// smaller, and a leading byte says which (<see cref="Raw"/> or <see cref="Brotli"/>). A lost key ring loses the texts, as
/// it loses the addresses.
/// <list type="bullet">
/// <item><see cref="ProtectTexts"/>: <c>Ballot.TextCipher</c>, all free text of one response as one JSON object
/// (<c>{"q5": "…", "q2.other": "…"}</c>), bound to the poll.</item>
/// <item><see cref="ProtectDraft"/>: <c>ResponseDraft.Cipher</c>, bound to the poll, the account and the response.</item>
/// </list>
/// Stored as <see cref="Prefix"/> + base64url, so no stored value holds an '@' (<c>EncryptedEmailTests</c>).
/// </summary>
public sealed class ResponseTextProtection(IDataProtectionProvider dataProtection)
{
    public const string TextPurpose = "Telrang.ResponseText.v1";
    public const string DraftPurpose = "Telrang.ResponseDraft.v1";
    public const string Prefix = "t1:";

    /// <summary>Leading byte of the packed plaintext.</summary>
    public const byte Raw = 0, Brotli = 1;

    /// <summary>Largest plaintext a packed value may unpack to (far above the 8 KiB of free text and any carry field).</summary>
    public const int MaxUnpackedBytes = 1024 * 1024;

    /// <summary>JSON for stored text: only quotes, backslashes and controls escaped, so the size stays close to the UTF-8 size.</summary>
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IDataProtector texts = dataProtection.CreateProtector(TextPurpose);
    private readonly IDataProtector drafts = dataProtection.CreateProtector(DraftPurpose);

    /// <summary>The <c>TextCipher</c> of a response's free text; null when it has none.</summary>
    public string? ProtectTexts(Guid pollId, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return null;
        return Prefix + Base64Url.EncodeToString(texts.CreateProtector(pollId.ToString("N")).Protect(Pack(JsonSerializer.SerializeToUtf8Bytes(values, Json))));
    }

    /// <summary>
    /// The free text of a stored response: empty for null; null when the value cannot be read (key ring lost, damaged,
    /// or written for another poll).
    /// </summary>
    public IReadOnlyDictionary<string, string>? RevealTexts(Guid pollId, string? stored)
    {
        if (stored is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        var plain = Open(texts.CreateProtector(pollId.ToString("N")), stored);
        if (plain is null) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(plain, Json);
            return map is null ? null : new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A draft's cipher: the packed and encrypted bytes, bound to poll, account and response.</summary>
    public string ProtectDraft(Guid pollId, string userId, int responseNo, byte[] plain) =>
        Prefix + Base64Url.EncodeToString(DraftProtector(pollId, userId, responseNo).Protect(Pack(plain)));

    /// <summary>The draft's plaintext, or null when it cannot be read.</summary>
    public byte[]? RevealDraft(Guid pollId, string userId, int responseNo, string stored) =>
        Open(DraftProtector(pollId, userId, responseNo), stored);

    private IDataProtector DraftProtector(Guid pollId, string userId, int responseNo) =>
        drafts.CreateProtector(pollId.ToString("N"), userId, responseNo.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static byte[]? Open(IDataProtector protector, string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            return Unpack(protector.Unprotect(Base64Url.DecodeFromChars(stored.AsSpan(Prefix.Length))), MaxUnpackedBytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The plaintext with its leading byte: Brotli-compressed when that is smaller, else as it is.</summary>
    public static byte[] Pack(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        using var buffer = new MemoryStream();
        buffer.WriteByte(Brotli);
        using (var brotli = new BrotliStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) brotli.Write(plain);
        if (buffer.Length < plain.Length + 1) return buffer.ToArray();
        var raw = new byte[plain.Length + 1];
        raw[0] = Raw;
        plain.CopyTo(raw, 1);
        return raw;
    }

    /// <summary>The plaintext of <see cref="Pack"/>; <see cref="InvalidDataException"/> for an unknown leading byte or more than <paramref name="max"/> bytes.</summary>
    public static byte[] Unpack(byte[] packed, int max)
    {
        ArgumentNullException.ThrowIfNull(packed);
        if (packed.Length == 0) throw new InvalidDataException("Empty value.");
        switch (packed[0])
        {
            case Raw:
                if (packed.Length - 1 > max) throw new InvalidDataException("Value too large.");
                return packed[1..];
            case Brotli:
                using (var brotli = new BrotliStream(new MemoryStream(packed, 1, packed.Length - 1), CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var chunk = new byte[16 * 1024];
                    int n;
                    while ((n = brotli.Read(chunk)) > 0)
                    {
                        if (output.Length + n > max) throw new InvalidDataException("Value too large.");
                        output.Write(chunk, 0, n);
                    }
                    return output.ToArray();
                }
            default:
                throw new InvalidDataException("Unknown packing.");
        }
    }

    /// <summary>UTF-8 helper for callers that pack JSON text.</summary>
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
