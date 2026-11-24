using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Telrang.Core.Polls;
using Telrang.Web.Security;

namespace Telrang.Web.Features.Voting;

/// <summary>
/// What a multi-page vote form carries between its pages (DESIGN.md §34.4): the page the posted fields belong to, the
/// answers of every page filled so far, and the name and notify fields. Never stored as a ballot; for an account it is
/// also the draft (<see cref="BallotService.SaveDraftAsync"/>).
/// </summary>
public sealed record CarryState(int Page, IReadOnlyDictionary<string, QuestionAnswer> Answers, string? Name = null, bool Notify = false)
{
    public static CarryState Empty => new(1, new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal));
}

/// <summary>
/// The hidden <c>_carry</c> field (DESIGN.md §34.4). Its content (<see cref="CarryState"/>) is packed like stored free text
/// (<see cref="ResponseTextProtection.Pack"/>) and protected with Data Protection under a purpose bound to the poll id, the
/// voter identity the request resolves to (<see cref="VoterIdentity.Stored"/>) and the response number (0 = a new
/// response), so a field only opens for the same voter, poll and response it was issued for. It is valid for
/// <see cref="Lifetime"/> from its issue (time from <see cref="TimeProvider"/>, inside the protected content) and at most
/// <see cref="VoteFormLimits.CarryFieldMax"/> long. A field that does not open (expired, another identity, poll or response,
/// forged, damaged) reads as null: the page starts the form over with one line. Opening it never trusts anything but its
/// content, and the answers in it are checked again on Submit like any posted answer.
/// </summary>
public sealed class VoteCarry(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public const string Field = "_carry";
    public const string Purpose = "Telrang.VoteCarry.v1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Shown above page 1 when a posted carry field does not open.</summary>
    public const string RestartMessage = "This form expired. Fill it in again.";

    private readonly IDataProtector root = dataProtection.CreateProtector(Purpose);

    private IDataProtector For(Guid pollId, VoterIdentity voter, int responseNo) =>
        root.CreateProtector(pollId.ToString("N"), voter.Stored, responseNo.ToString(CultureInfo.InvariantCulture));

    /// <summary>The field's value for <paramref name="state"/>; null when the content is over <see cref="VoteFormLimits.CarryMaxBytes"/>.</summary>
    public string? Issue(Guid pollId, VoterIdentity voter, int responseNo, PollDefinition def, CarryState state)
    {
        var packed = ResponseTextProtection.Pack(Serialize(def, state, time.GetUtcNow()));
        if (packed.Length > VoteFormLimits.CarryMaxBytes) return null;
        return Base64Url.EncodeToString(For(pollId, voter, responseNo).Protect(packed));
    }

    /// <summary>The content of a posted field, or null when it does not open or has expired.</summary>
    public CarryState? Open(Guid pollId, VoterIdentity voter, int responseNo, PollDefinition def, string? field)
    {
        if (string.IsNullOrEmpty(field) || field.Length > VoteFormLimits.CarryFieldMax) return null;
        byte[] plain;
        try
        {
            plain = ResponseTextProtection.Unpack(For(pollId, voter, responseNo).Unprotect(Base64Url.DecodeFromChars(field)), ResponseTextProtection.MaxUnpackedBytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or InvalidDataException)
        {
            return null;
        }
        return Deserialize(def, plain, time.GetUtcNow(), Lifetime);
    }

    /// <summary>The content as JSON: <c>{"at": unix seconds, "page": n, "p": {scored parts}, "t": {free text}, "n": name, "y": notify}</c>.</summary>
    public static byte[] Serialize(PollDefinition def, CarryState state, DateTimeOffset now)
    {
        var o = new JsonObject
        {
            ["at"] = now.ToUnixTimeSeconds(),
            ["page"] = state.Page,
            ["p"] = JsonNode.Parse(ResponseAnswers.Payload(def, state.Answers)),
            ["t"] = JsonSerializer.SerializeToNode(ResponseAnswers.Texts(def, state.Answers), ResponseTextProtection.Json),
        };
        if (state.Name is { } name) o["n"] = name;
        if (state.Notify) o["y"] = true;
        return JsonSerializer.SerializeToUtf8Bytes(o, ResponseTextProtection.Json);
    }

    /// <summary>
    /// Reads <see cref="Serialize"/>'s JSON against the current definition (questions and options it no longer has are
    /// dropped); null when it is no carry content or older than <paramref name="lifetime"/> (null = any age, for drafts).
    /// </summary>
    public static CarryState? Deserialize(PollDefinition def, byte[] json, DateTimeOffset now, TimeSpan? lifetime)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("at", out var at) || !at.TryGetInt64(out var seconds)
                || !r.TryGetProperty("page", out var pg) || !pg.TryGetInt32(out var page)) return null;
            if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
            var issued = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (lifetime is { } life && (now - issued > life || issued - now > TimeSpan.FromMinutes(5))) return null;
            var payload = r.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Object ? p.GetRawText() : "{}";
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (r.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var e in t.EnumerateObject())
                    if (e.Value.ValueKind == JsonValueKind.String) texts[e.Name] = e.Value.GetString()!;
            var name = r.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var notify = r.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.True;
            return new CarryState(page, ResponseAnswers.Read(def, payload, texts), name, notify);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
