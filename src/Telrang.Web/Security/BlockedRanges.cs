using System.Net;
using Telrang.Web.Data;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Security;

public sealed record BlockedRangeRow(Guid Id, string Cidr, string? Note, DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? ExpiresAt);

/// <summary>
/// Persistent CIDR blocks (DESIGN.md §17): rows of <see cref="BlockedRange"/>, typed in by admins, held in memory
/// as parsed networks and reloaded after every change. <see cref="AbuseBlockMiddleware"/> checks them before the
/// in-memory tracker. Expiry is checked on every match, so an expired row stops blocking at once; the row itself
/// is purged on the next load. Refuses ranges that cover loopback or the trusted reverse proxies.
/// </summary>
public sealed class BlockedRangeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider time,
    IOptions<ForwardedHeadersOptions> forwarded,
    EmailProtection addresses)
{
    public const int MaxRows = 1000;

    /// <summary>Shortest prefixes accepted: an IPv4 /16 (65,536 addresses), an IPv6 /32 (a large provider's allocation).</summary>
    public const int MinPrefixV4 = 16, MinPrefixV6 = 32;

    /// <summary>
    /// The way out of a lock-out (pentest R2-ACC-7): <c>Abuse:ClearBlockedRangesOnStart=true</c> removes every range at
    /// startup. Returns how many rows were removed.
    /// </summary>
    public async Task<int> ClearAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.BlockedRanges.ExecuteDeleteAsync(ct);
        Invalidate();
        return removed;
    }

    private static readonly IPNetwork[] Loopback = [IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("::1/128")];

    private sealed record Entry(IPNetwork Network, DateTimeOffset? ExpiresAt);

    private readonly Lock loadLock = new();
    private volatile IReadOnlyList<Entry>? entries;

    private IReadOnlyList<Entry> Entries
    {
        get
        {
            if (entries is { } e) return e;
            lock (loadLock)
            {
                if (entries is { } again) return again;
                using var db = dbFactory.CreateDbContext();
                var rows = db.BlockedRanges.AsNoTracking().Select(r => new { r.Cidr, r.ExpiresAt }).ToList();
                var list = new List<Entry>(rows.Count);
                foreach (var r in rows)
                {
                    if (IPNetwork.TryParse(r.Cidr, out var net)) list.Add(new Entry(net, r.ExpiresAt));
                }
                return entries = list;
            }
        }
    }

    /// <summary>True when <paramref name="address"/> lies in a range that has not expired.</summary>
    public bool IsBlocked(IPAddress? address)
    {
        if (address is null) return false;
        var list = Entries;
        if (list.Count == 0) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var now = time.GetUtcNow();
        foreach (var e in list)
        {
            if ((e.ExpiresAt is null || e.ExpiresAt > now) && e.Network.Contains(address)) return true;
        }
        return false;
    }

    public void Invalidate() => entries = null;

    /// <summary>All rows, newest first, after purging expired ones. Creator emails resolved.</summary>
    public async Task<IReadOnlyList<BlockedRangeRow>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var rows = await db.BlockedRanges.ToListAsync(ct); // ≤ 1000; SQLite cannot compare DateTimeOffset in SQL
        var expired = rows.Where(r => r.ExpiresAt is { } x && x <= now).ToList();
        if (expired.Count > 0)
        {
            db.BlockedRanges.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
            Invalidate();
        }
        var live = rows.Except(expired).ToList();
        var creatorIds = live.Select(r => r.CreatedByUserId).OfType<string>().Distinct().ToList();
        var emails = await db.Users.AsNoTracking().Where(u => creatorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        return live
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new BlockedRangeRow(r.Id, r.Cidr, r.Note, r.CreatedAt, r.CreatedByUserId is { } id ? addresses.Display(emails.GetValueOrDefault(id)) ?? "deleted account" : null, r.ExpiresAt))
            .ToList();
    }

    /// <summary>Validates and stores a range. Returns (row, null) or (null, error line).</summary>
    /// <param name="actor">The acting admin's current address (null when unknown): a range that contains it is refused.</param>
    public async Task<(BlockedRangeRow? Row, string? Error)> AddAsync(string? cidr, string? note, TimeSpan? duration, string? createdBy,
        CancellationToken ct = default, IPAddress? actor = null)
    {
        if (!AbuseTracker.TryParseNetwork(cidr, out var network, out var hostBits)) return (null, "Enter a CIDR such as 203.0.113.0/24 or 2001:db8::/48, or one address.");
        if (network.PrefixLength == 0) return (null, "That range covers every address.");
        // A typo such as 198.51.100.7/1 must not block half the internet (pentest R2-ACC-7): host bits and very short
        // prefixes are refused, not silently widened.
        if (hostBits) return (null, $"Use {network}.");
        var v4 = network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        if (network.PrefixLength < (v4 ? MinPrefixV4 : MinPrefixV6)) return (null, $"Use a prefix of /{(v4 ? MinPrefixV4 : MinPrefixV6)} or longer.");
        if (Loopback.Any(l => Overlaps(l, network))) return (null, "That range includes loopback.");
        if (actor is not null && network.Contains(actor.IsIPv4MappedToIPv6 ? actor.MapToIPv4() : actor)) return (null, "That range includes your own address.");
        var options = forwarded.Value;
        if (options.KnownProxies.Any(p => network.Contains(p.IsIPv4MappedToIPv6 ? p.MapToIPv4() : p))
            || options.KnownIPNetworks.Any(n => Overlaps(n, network)))
            return (null, "That range includes a trusted reverse proxy.");
        if (duration is { } d && d <= TimeSpan.Zero) return (null, "Duration must be positive.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > BlockedRange.MaxNoteLength }) return (null, $"Note: {BlockedRange.MaxNoteLength} characters at most.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = network.ToString();
        if (await db.BlockedRanges.AnyAsync(r => r.Cidr == key, ct)) return (null, $"{key} is already blocked.");
        if (await db.BlockedRanges.CountAsync(ct) >= MaxRows) return (null, $"At most {MaxRows} ranges. Remove some first.");
        var now = time.GetUtcNow();
        var row = new BlockedRange
        {
            Id = Guid.NewGuid(), Cidr = key, Note = note, CreatedAt = now, CreatedByUserId = createdBy,
            ExpiresAt = duration is { } dd ? now + dd : null,
        };
        db.BlockedRanges.Add(row);
        await db.SaveChangesAsync(ct);
        Invalidate();
        return (new BlockedRangeRow(row.Id, row.Cidr, row.Note, row.CreatedAt, null, row.ExpiresAt), null);
    }

    /// <summary>Removes a range; returns its CIDR, or null when it did not exist.</summary>
    public async Task<string?> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.BlockedRanges.FindAsync([id], ct);
        if (row is null) return null;
        db.BlockedRanges.Remove(row);
        await db.SaveChangesAsync(ct);
        Invalidate();
        return row.Cidr;
    }

    /// <summary>Two networks overlap when one contains the other's base address (prefixes are nested or disjoint).</summary>
    private static bool Overlaps(IPNetwork a, IPNetwork b) =>
        a.BaseAddress.AddressFamily == b.BaseAddress.AddressFamily && (a.Contains(b.BaseAddress) || b.Contains(a.BaseAddress));
}
