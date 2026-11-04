using System.Buffers.Text;

namespace Telrang.Core.Ids;

/// <summary>Poll ids: random v4 Guid &lt;-&gt; 22-char base64url (no padding) of its 16 bytes.</summary>
public static class PollId
{
    public const int EncodedLength = 22;

    public static string Encode(Guid id) => Base64Url.EncodeToString(id.ToByteArray());

    public static bool TryDecode(string? value, out Guid id)
    {
        id = default;
        if (value is null || value.Length != EncodedLength) return false;
        foreach (var c in value)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return false;
        }
        try
        {
            var bytes = Base64Url.DecodeFromChars(value);
            if (bytes.Length != 16) return false;
            var guid = new Guid(bytes);
            // Reject non-canonical encodings (non-zero trailing bits) so each id has one spelling.
            if (Encode(guid) != value) return false;
            id = guid;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Deliberately Guid.NewGuid() (random v4) and NOT Guid.CreateVersion7(): v7 ids are
    // time-ordered, which makes public poll ids guessable/enumerable.
    public static Guid NewId() => Guid.NewGuid();
}
