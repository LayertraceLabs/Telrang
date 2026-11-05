using System.Text;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Polls;

/// <summary>Retention rule (DESIGN.md §14.9): when a poll is deleted automatically.</summary>
public static class Retention
{
    /// <summary>Longer than any span between two representable instants (10 000 years).</summary>
    private const int MaxDays = 3_652_500;

    /// <summary>
    /// The instant a poll closed (or will close by its date) plus <paramref name="days"/>; null when retention is off
    /// (0), the poll has no close at all or the sum is past the calendar. A poll is deleted once this has passed.
    /// </summary>
    public static DateTimeOffset? DeleteOn(DateTimeOffset? closedAt, DateTimeOffset? closesAt, int days)
    {
        if (days <= 0 || PollState.ClosedOn(closedAt, closesAt) is not { } closed) return null;
        // A close near year 9999 (or a huge setting) would overflow: such a poll is never due (AV-14).
        var span = TimeSpan.FromDays(Math.Min(days, MaxDays));
        return DateTimeOffset.MaxValue - closed < span ? null : closed + span;
    }
}

/// <summary>
/// One background timer (every 5 minutes) for two poll sweeps:
/// <list type="bullet">
/// <item>Results ready: polls that closed since the last pass (<see cref="NotifyDue"/>: never mailed, or closed again
/// and the last mailing a day old) get one
/// <see cref="MailType.ResultsReady"/> mail per distinct account that ticked "Email me when this poll closes" on any
/// of its responses (the flag is per response, the mail per account), when that account may act with a verified
/// address and can still see the results (the same <see cref="PollAccessService"/> check as the results page, as that
/// account). Each poll is claimed with a conditional update first, so no close is mailed twice.</item>
/// <item>Retention (at most once a day): polls closed more than <c>Retention.DeleteClosedAfterDays</c> days ago are
/// deleted with their ballots and invites. Off (0) by default. The same daily pass deletes self-registered accounts
/// left unverified for 7 days.</item>
/// </list>
/// <c>Polls:BackgroundSweep=false</c> turns the timer off (tests call the passes directly).
/// </summary>
public sealed class PollSweepService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SettingsService settings,
    IMailQueue mail,
    TimeProvider time,
    IDataProtectionProvider dataProtection,
    VoterKeys voterKeys,
    IConfiguration config,
    ILogger<PollSweepService> logger,
    IServiceScopeFactory scopes,
    Results.PollChangeNotifier notifier,
    Security.AuthSessionStore sessions,
    Security.EmailProtection emails) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetentionInterval = TimeSpan.FromDays(1);
    public const string UnsubscribePurpose = "Telrang.NotifyOff.v1";

    private readonly IDataProtector unsubscribe = dataProtection.CreateProtector(UnsubscribePurpose);
    private readonly SemaphoreSlim running = new(1, 1);
    private DateTimeOffset? lastRetention;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Polls:BackgroundSweep", true)) return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Poll sweep failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One timer tick: the results-ready pass, then retention when a day has passed since its last run.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await running.WaitAsync(ct);
        try
        {
            await NotifyClosedAsync(ct);
            var now = time.GetUtcNow();
            if (lastRetention is null || now - lastRetention >= RetentionInterval)
            {
                await DeleteExpiredAsync(ct);
                await PurgeUnverifiedAccountsAsync(ct);
                await sessions.PurgeExpiredAsync(ct); // server-side sessions past their expiry (R3-DEP-2)
                lastRetention = now;
            }
        }
        finally
        {
            running.Release();
        }
    }

    /// <summary>Daily: self-registered accounts still unverified after 7 days are deleted (pentest AV-10, AccountService).</summary>
    public async Task<int> PurgeUnverifiedAccountsAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Accounts.AccountService>().PurgeUnverifiedAsync(ct);
    }

    /// <summary>
    /// Mails every subscriber of every poll that closed since the last pass. Returns the number of mails queued.
    /// Does nothing (and marks nothing) while mail or the public URL is not configured, so those closes are
    /// handled once both are set.
    /// </summary>
    public async Task<int> NotifyClosedAsync(CancellationToken ct = default)
    {
        var s = settings.Current;
        if (!s.Mail.IsConfigured || s.PublicUrl is not { } publicUrl) return 0;
        var linkBase = publicUrl.TrimEnd('/') + "/";
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = (await NotifyCandidates(db, now)
                .Select(p => new { p.Id, p.Title, p.ClosedAt, p.ClosesAt, p.ClosedNotifiedAt }).ToListAsync(ct))
            .Where(p => NotifyDue(p.ClosedAt, p.ClosesAt, p.ClosedNotifiedAt, now))
            .ToList();
        var access = new PollAccessService(dbFactory, time, voterKeys);
        var queued = 0;
        foreach (var poll in candidates)
        {
            var notifiedBefore = poll.ClosedNotifiedAt;
            var claimed = await db.Polls.Where(p => p.Id == poll.Id && p.ClosedNotifiedAt == notifiedBefore)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.ClosedNotifiedAt, now), ct);
            if (claimed != 1) continue; // another pass got it
            // A close mail days late (mail or the public URL was off at the time) is noise: marked, not sent.
            if (now - PollState.ClosedOn(poll.ClosedAt, poll.ClosesAt) > NotifyMaxAge) continue;
            var pollId = poll.Id;
            var recipients = await (from b in db.Ballots
                                    where b.PollId == pollId && b.NotifyOnClose && b.UserId != null
                                    join u in db.Users on b.UserId equals u.Id
                                    select new { u.Id, u.Email }).Distinct().ToListAsync(ct);
            var encoded = PollId.Encode(pollId);
            foreach (var r in recipients.DistinctBy(r => r.Id))
            {
                // Per recipient, as that account: it may still act with a verified address and still reach the results
                // (a revoked invitation, a transfer away from a password poll or a password change take that away).
                if (emails.Reveal(r.Email) is not { } address || !await AccountGate.MayActAsync(db, r.Id, ct, verifiedEmail: true)) continue;
                if (await access.GetAsync(db, encoded, VisitorContext.ForUser(r.Id), tracking: false, ct) is not { CanSeeResults: true }) continue;
                var body = new StringBuilder()
                    .Append('“').Append(poll.Title).Append("” has closed.\n\n")
                    .Append("Results: ").Append(linkBase).Append("p/").Append(encoded).Append("/results\n\n")
                    .Append("Stop emails about this poll: ").Append(linkBase).Append("notify/off/").Append(UnsubscribeToken(r.Id, pollId)).Append('\n')
                    .ToString();
                if (await mail.EnqueueAsync(address, $"Poll closed: {poll.Title}", body, MailType.ResultsReady, ct: ct)) queued++;
            }
        }
        return queued;
    }

    /// <summary>Closes older than this when the sweep first sees them are marked handled without mail.</summary>
    public static readonly TimeSpan NotifyMaxAge = TimeSpan.FromDays(7);

    /// <summary>Shortest time between two results-ready mailings of one poll (R2-POLLS-3).</summary>
    public static readonly TimeSpan NotifyInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The polls <see cref="NotifyDue"/> can hold at <paramref name="now"/>, narrowed as far as SQL can on both providers:
    /// one with a close at all, and not mailed within <see cref="NotifyInterval"/> (<c>ClosedNotifiedAt</c> is stored as a
    /// comparable long). <c>ClosedAt</c> and <c>ClosesAt</c> are plain <see cref="DateTimeOffset"/> columns, which SQLite
    /// cannot compare, so "closed" and "closed again since the last mailing" are decided in memory.
    /// </summary>
    public static IQueryable<Poll> NotifyCandidates(ApplicationDbContext db, DateTimeOffset now)
    {
        var mailedBefore = now - NotifyInterval;
        return db.Polls.AsNoTracking().Where(p => (p.ClosedAt != null || p.ClosesAt != null)
            && (p.ClosedNotifiedAt == null || p.ClosedNotifiedAt <= mailedBefore));
    }

    /// <summary>
    /// A poll's close is mailed when it is closed and was never mailed, or closed again after its last mailing and that
    /// mailing is at least <see cref="NotifyInterval"/> old. Reopening never re-arms it sooner (R2-POLLS-3: close/reopen
    /// cycles mailed every voter on every sweep); a close inside the interval is mailed once the interval has passed.
    /// </summary>
    public static bool NotifyDue(DateTimeOffset? closedAt, DateTimeOffset? closesAt, DateTimeOffset? notifiedAt, DateTimeOffset now)
    {
        if (!PollState.IsClosed(closedAt, closesAt, now)) return false;
        if (notifiedAt is not { } last) return true;
        return PollState.ClosedOn(closedAt, closesAt) > last && now - last >= NotifyInterval;
    }

    /// <summary>Deletes polls whose <see cref="Retention.DeleteOn"/> has passed. Returns how many.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken ct = default)
    {
        var days = settings.Current.RetentionDays;
        if (days <= 0) return 0;
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = new List<Guid>();
        // A poll without any close is never due (Retention.DeleteOn); the close instants are compared in memory (SQLite).
        foreach (var p in await db.Polls.AsNoTracking().Where(p => p.ClosedAt != null || p.ClosesAt != null)
                     .Select(p => new { p.Id, p.ClosedAt, p.ClosesAt }).ToListAsync(ct))
        {
            // Each poll on its own: one row that cannot be evaluated never stops retention for the rest (AV-14).
            try
            {
                if (Retention.DeleteOn(p.ClosedAt, p.ClosesAt, days) is { } d && d <= now) due.Add(p.Id);
            }
            catch (Exception e) when (e is ArgumentException or OverflowException)
            {
                logger.LogWarning(e, "Retention: skipped poll {PollId}, its close time cannot be evaluated.", PollId.Encode(p.Id));
            }
        }
        if (due.Count == 0) return 0;
        await using var tx = await db.Database.BeginWriteAsync(ct);
        var deleted = await PollService.DeleteCascadeAsync(db, due, ct);
        await tx.CommitAsync(ct);
        foreach (var id in due) notifier.End(id);
        logger.LogInformation("Retention: deleted {Count} polls closed more than {Days} days ago.", deleted, days);
        return deleted;
    }

    /// <summary>The <c>/notify/off/{token}</c> token: Data Protection payload of the account and poll ids.</summary>
    public string UnsubscribeToken(string userId, Guid pollId) => unsubscribe.Protect($"{pollId:N}|{userId}");

    /// <summary>The poll and account of an unsubscribe token; null when it does not unprotect (tampered, or the key ring was lost).</summary>
    public (Guid PollId, string UserId)? ReadUnsubscribeToken(string? token)
    {
        string payload;
        try { payload = unsubscribe.Unprotect(token ?? ""); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (FormatException) { return null; }
        var parts = payload.Split('|', 2);
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var pollId) ? (pollId, parts[1]) : null;
    }

    /// <summary>
    /// Clears "Email me when this poll closes" on the account's responses in the poll. Idempotent; false only for a
    /// token that does not unprotect (tampered, or the key ring was lost).
    /// </summary>
    public async Task<bool> UnsubscribeAsync(string? token, CancellationToken ct = default)
    {
        if (ReadUnsubscribeToken(token) is not var (pollId, userId)) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Ballots.Where(b => b.PollId == pollId && b.UserId == userId && b.NotifyOnClose)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.NotifyOnClose, false), ct);
        return true;
    }
}
