using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Security;

/// <summary>Bound from the <c>Abuse</c> configuration section (DESIGN.md §14.7).</summary>
public sealed class AbuseOptions
{
    public const string Section = "Abuse";

    /// <summary>Signal weight within <see cref="Window"/> that triggers a block.</summary>
    public int Threshold { get; set; } = 20;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Block durations by escalation level (first, second, third and later).</summary>
    public TimeSpan[] Durations { get; set; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(60)];

    /// <summary>Without signals or blocks for this long, a key's escalation level resets.</summary>
    public TimeSpan QuietReset { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Signed form stamps younger than this are refused (bots post instantly).</summary>
    public double FormMinSeconds { get; set; } = 2;

    /// <summary>Signed form stamps older than this are refused.</summary>
    public double FormMaxMinutes { get; set; } = 120;
}

/// <summary>Things that count towards a temporary block.</summary>
/// <remarks><see cref="CircuitOutOfMemory"/>: an interactive event ran the process out of memory (DESIGN.md §31, pentest
/// R3-RES-7b), recorded against the circuit's client key and account.</remarks>
public enum AbuseSignal { FailedLogin, RateLimited, Honeypot, FormTimestamp, LimitRefused, Captcha, CircuitOutOfMemory, FormTooLarge, Antiforgery }

public enum AbuseKind { Ip, Account }

/// <param name="Key">Client key (IPv4 address or IPv6 /48) or account id.</param>
/// <param name="Label">What the admin sees (email for accounts when known).</param>
public sealed record AbuseBlock(string Id, AbuseKind Kind, string Key, string Reason, DateTimeOffset Until, int Count, bool Manual, string? Note,
    string? Label = null);

/// <summary>
/// In-memory abuse tracker (DESIGN.md §14.7, §15): counts signals per client key (IPv4 /32, IPv6 /48)
/// and per account, and blocks a key for 1, then 10, then 60 minutes once the weighted signals in the
/// window cross the threshold. A key that stays quiet for 24 h starts again at 1 minute. Admins can
/// list and clear blocks and add manual account blocks. Networks are blocked only through the persistent
/// <see cref="BlockedRangeService"/> (one mechanism for addresses, with its safety checks). Nothing here is
/// persisted: a restart clears everything, and no address is ever written anywhere else.
/// </summary>
public sealed class AbuseTracker(TimeProvider time, IOptions<AbuseOptions> options)
{
    /// <summary>Hard cap on tracked keys (pentest R2-ACC-6): past it the least recently seen are dropped, so memory is bounded.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>What eviction trims to, so it runs once per <c>MaxEntries / 10</c> new keys, not on every one.</summary>
    private const int TrimTo = MaxEntries * 9 / 10;

    private sealed class Entry
    {
        public readonly Queue<(DateTimeOffset At, int Weight)> Signals = new();
        public int Level;
        public int Total;
        public DateTimeOffset LastActivity;
        public DateTimeOffset? BlockedUntil;
        public string Reason = "";
    }

    private sealed record Manual(string Id, string AccountId, DateTimeOffset Until, string? Note, string? Label);

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly ConcurrentDictionary<string, Manual> manual = new();

    private AbuseOptions Options => options.Value;

    public static int Weight(AbuseSignal signal) => signal switch
    {
        AbuseSignal.Honeypot => 10,
        // A body or form over the request limits: the app's own forms never send one.
        AbuseSignal.FormTimestamp or AbuseSignal.Captcha or AbuseSignal.FormTooLarge => 2,
        // Strong but not alone decisive: memory may run out under someone else's load too.
        AbuseSignal.CircuitOutOfMemory => 5,
        _ => 1,
    };

    private static string IpEntryKey(string clientKey) => "ip:" + clientKey;
    private static string AccountEntryKey(string accountId) => "acct:" + accountId;
    private static string ManualAccountKey(string accountId) => "manual:" + accountId;

    /// <summary>Records one signal for the client key and/or the account (either may be null).</summary>
    public void Record(string? clientKey, string? accountId, AbuseSignal signal)
    {
        if (clientKey is { Length: > 0 } && clientKey != "unknown") Add(IpEntryKey(clientKey), signal);
        if (accountId is { Length: > 0 }) Add(AccountEntryKey(accountId), signal);
    }

    private void Add(string key, AbuseSignal signal)
    {
        if (entries.Count >= MaxEntries && !entries.ContainsKey(key)) Sweep();
        var entry = entries.GetOrAdd(key, _ => new Entry());
        var now = time.GetUtcNow();
        var o = Options;
        lock (entry)
        {
            if (entry.BlockedUntil is { } until && until > now) return; // already blocked: do not escalate further
            if (now - entry.LastActivity >= o.QuietReset) entry.Level = 0;
            entry.LastActivity = now;
            Prune(entry, now);
            entry.Signals.Enqueue((now, Weight(signal)));
            entry.Total++;
            if (entry.Signals.Sum(s => s.Weight) >= o.Threshold)
            {
                var duration = o.Durations[Math.Min(entry.Level, o.Durations.Length - 1)];
                entry.BlockedUntil = now + duration;
                entry.Reason = signal.ToString();
                entry.Level++;
                entry.Signals.Clear();
            }
        }
    }

    private void Prune(Entry entry, DateTimeOffset now)
    {
        var cutoff = now - Options.Window;
        while (entry.Signals.Count > 0 && entry.Signals.Peek().At <= cutoff) entry.Signals.Dequeue();
    }

    /// <summary>The block that applies to this client address or account, or null.</summary>
    public AbuseBlock? Find(IPAddress? address, string? accountId)
    {
        var now = time.GetUtcNow();
        if (address is not null)
        {
            var key = ClientKeys.ForAbuse(address);
            if (Active(IpEntryKey(key), now) is { } ip) return ip;
        }
        if (accountId is { Length: > 0 })
        {
            if (Active(AccountEntryKey(accountId), now) is { } account) return account;
            if (manual.TryGetValue(ManualAccountKey(accountId), out var m) && m.Until > now) return ToBlock(m);
        }
        return null;
    }

    private AbuseBlock? Active(string key, DateTimeOffset now)
    {
        if (!entries.TryGetValue(key, out var e)) return null;
        lock (e)
        {
            return e.BlockedUntil is { } until && until > now ? ToBlock(key, e, until) : null;
        }
    }

    private static AbuseBlock ToBlock(string entryKey, Entry e, DateTimeOffset until)
    {
        var account = entryKey.StartsWith("acct:", StringComparison.Ordinal);
        return new AbuseBlock(entryKey, account ? AbuseKind.Account : AbuseKind.Ip, entryKey[(entryKey.IndexOf(':') + 1)..], e.Reason, until, e.Total,
            Manual: false, Note: null);
    }

    private static AbuseBlock ToBlock(Manual m) => new(m.Id, AbuseKind.Account, m.AccountId, "Manual", m.Until, 0, Manual: true, m.Note, m.Label);

    /// <summary>Current blocks (automatic and manual), soonest expiry last.</summary>
    public IReadOnlyList<AbuseBlock> List()
    {
        var now = time.GetUtcNow();
        var list = new List<AbuseBlock>();
        foreach (var (key, e) in entries)
        {
            lock (e)
            {
                if (e.BlockedUntil is { } until && until > now) list.Add(ToBlock(key, e, until));
            }
        }
        foreach (var m in manual.Values)
        {
            if (m.Until > now) list.Add(ToBlock(m));
            else manual.TryRemove(new KeyValuePair<string, Manual>(m.Id, m));
        }
        return list.OrderByDescending(b => b.Until).ToList();
    }

    public int ActiveCount => List().Count;

    /// <summary>Tracked keys (addresses and accounts with recent signals); at most about <see cref="MaxEntries"/>.</summary>
    public int TrackedCount => entries.Count;

    /// <summary>Ends a block. For automatic blocks the escalation level stays (it resets after 24 h quiet).</summary>
    public bool Clear(string id)
    {
        if (manual.TryRemove(id, out _)) return true;
        if (!entries.TryGetValue(id, out var e)) return false;
        lock (e)
        {
            if (e.BlockedUntil is null) return false;
            e.BlockedUntil = null;
            e.Signals.Clear();
            return true;
        }
    }

    /// <summary>Manual block of an account.</summary>
    public AbuseBlock AddAccount(string accountId, string? label, TimeSpan duration, string? note)
    {
        var m = new Manual(ManualAccountKey(accountId), accountId, time.GetUtcNow() + duration, Trim(note), label);
        manual[m.Id] = m;
        return ToBlock(m);
    }

    /// <param name="hostBits">The text had bits set past the prefix ("203.0.113.7/24"); <paramref name="network"/> is then the masked network.</param>
    public static bool TryParseNetwork(string? text, out IPNetwork network, out bool hostBits)
    {
        network = default;
        hostBits = false;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!text.Contains('/'))
        {
            if (!IPAddress.TryParse(text, out var single)) return false;
            if (single.IsIPv4MappedToIPv6) single = single.MapToIPv4();
            network = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }
        // Host bits set ("203.0.113.7/24"): masked, and reported so the caller can refuse it.
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var addr)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix)) return false;
        var original = addr.GetAddressBytes();
        if (prefix > original.Length * 8) return false;
        var bytes = (byte[])original.Clone();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        hostBits = !bytes.AsSpan().SequenceEqual(original);
        network = new IPNetwork(new IPAddress(bytes), prefix);
        return true;
    }

    private static string? Trim(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim() is { Length: > 200 } n ? n[..200] : note.Trim();

    private int sweeping;

    private void Sweep()
    {
        if (Interlocked.Exchange(ref sweeping, 1) == 1) return; // another caller is trimming already
        try
        {
            SweepCore();
        }
        finally
        {
            Volatile.Write(ref sweeping, 0);
        }
    }

    private void SweepCore()
    {
        var now = time.GetUtcNow();
        foreach (var (key, e) in entries)
        {
            lock (e)
            {
                if (now - e.LastActivity >= Options.QuietReset && !(e.BlockedUntil > now))
                    entries.TryRemove(new KeyValuePair<string, Entry>(key, e));
            }
        }
        if (entries.Count < MaxEntries) return;

        // Still full (many keys, e.g. spread over IPv6 prefixes): drop the least recently seen, unblocked ones first,
        // so the memory stays bounded and an active block is the last thing to go.
        var victims = entries
            .Select(kv => { lock (kv.Value) return (kv.Key, kv.Value, Blocked: kv.Value.BlockedUntil > now, kv.Value.LastActivity); })
            .OrderBy(v => v.Blocked)
            .ThenBy(v => v.LastActivity)
            .Take(entries.Count - TrimTo)
            .ToList();
        foreach (var (key, e, _, _) in victims) entries.TryRemove(new KeyValuePair<string, Entry>(key, e));
    }
}

/// <summary>Client keys for per-client state: IPv4 per address, IPv6 per prefix (a client usually owns a /64 or more).</summary>
public static class ClientKeys
{
    public static string For(IPAddress? ip, int ipv6PrefixBytes = 8)
    {
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4().ToString();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            Array.Clear(bytes, ipv6PrefixBytes, 16 - ipv6PrefixBytes);
            return new IPAddress(bytes) + "/" + (ipv6PrefixBytes * 8);
        }
        return ip.ToString();
    }

    /// <summary>The abuse tracker's key: IPv4 /32, IPv6 /48.</summary>
    public static string ForAbuse(IPAddress? ip) => For(ip, ipv6PrefixBytes: 6);

    public static string ForAbuse(HttpContext ctx) => ForAbuse(ctx.Connection.RemoteIpAddress);
}
