using System.Buffers.Text;
using System.Security.Cryptography;

namespace Telrang.Web.Features.Voting;

/// <summary>
/// Single-use vote form ids (DESIGN.md §21.1): every rendered vote form carries a random id; a second POST with an id
/// the same voter identity already used is answered with that response instead of creating another one (double click,
/// a resent POST). Memory only, bounded (<see cref="Capacity"/> entries, each kept <see cref="Lifetime"/>, the stamp's
/// maximum age, so an older form is refused anyway).
/// </summary>
public sealed class VoteFormIds(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    public const int Capacity = 50_000;

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, long Serial, DateTimeOffset At)> order = new();
    private long serial;

    private sealed class Entry(long serial, DateTimeOffset at)
    {
        public long Serial { get; } = serial;
        public DateTimeOffset At { get; } = at;
        /// <summary>The response saved under this id; null while the first POST is still being written.</summary>
        public int? ResponseNo { get; set; }
    }

    /// <summary>A new random id (128 bits, base64url).</summary>
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Only ids shaped like <see cref="Create"/>'s count; anything else is ignored (no deduplication).</summary>
    public static bool IsWellFormed(string? id) =>
        id is { Length: 22 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Key(Guid pollId, string identity, string formId) => pollId.ToString("N") + "|" + identity + "|" + formId;

    /// <summary>
    /// Reserves the id for this voter. False when it was used already; <paramref name="responseNo"/> is then the response
    /// it saved (null while that POST is still in flight).
    /// </summary>
    public bool TryReserve(Guid pollId, string identity, string formId, out int? responseNo)
    {
        var key = Key(pollId, identity, formId);
        var now = time.GetUtcNow();
        lock (gate)
        {
            Prune(now);
            if (entries.TryGetValue(key, out var existing))
            {
                responseNo = existing.ResponseNo;
                return false;
            }
            var entry = new Entry(++serial, now);
            entries[key] = entry;
            order.Enqueue((key, entry.Serial, now));
            responseNo = null;
            return true;
        }
    }

    /// <summary>The POST that reserved the id saved this response.</summary>
    public void Complete(Guid pollId, string identity, string formId, int responseNo)
    {
        lock (gate)
        {
            if (entries.TryGetValue(Key(pollId, identity, formId), out var entry)) entry.ResponseNo = responseNo;
        }
    }

    /// <summary>The POST that reserved the id saved nothing (refused, failed): the same form may be sent again.</summary>
    public void Release(Guid pollId, string identity, string formId)
    {
        lock (gate)
        {
            entries.Remove(Key(pollId, identity, formId));
        }
    }

    public int Count
    {
        get { lock (gate) return entries.Count; }
    }

    /// <summary>Queued expiry records (live and stale); never more than <see cref="Capacity"/>.</summary>
    public int Queued
    {
        get { lock (gate) return order.Count; }
    }

    private void Prune(DateTimeOffset now)
    {
        // Bounded by the queue, not the dictionary: a released id leaves its queue record behind (pentest R2-VOTE-1).
        while (order.Count > 0 && (order.Peek().At < now - Lifetime || order.Count >= Capacity))
        {
            var (key, s, _) = order.Dequeue();
            if (entries.TryGetValue(key, out var e) && e.Serial == s) entries.Remove(key);
        }
    }
}
