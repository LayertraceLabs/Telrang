using Telrang.Web.Data;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Mail;

public sealed record MailQueueStats(int Queued, int Dead, string? LastError, DateTimeOffset? LastErrorAt);

public sealed record MailLogRow(string To, MailType Type, string State, DateTimeOffset At);

/// <summary>
/// Background sender (DESIGN.md §14.5): every 10 s sends due messages, at most
/// <c>Mail.HourlyCap</c> per rolling hour. Failures retry after 1, 5, 15 and 60 minutes; the fifth
/// failure dead-letters the message (every step is used). A sent or dead message loses its bodies at once, and sent
/// rows go after <see cref="SentRetention"/> (pentest AV-04: the outbox held live setup and reset links); a message still
/// unsent after <see cref="UnsentLifetime"/> (mail unconfigured since) is dead-lettered without them. <c>Mail:BackgroundSender=false</c> turns the loop off (tests
/// call <see cref="ProcessDueAsync"/> directly).
/// </summary>
public sealed class MailSender(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SettingsService settings,
    IMailTransport transport,
    TimeProvider time,
    IConfiguration config,
    ILogger<MailSender> logger,
    Telrang.Web.Features.Status.AppMetrics metrics,
    Security.EmailProtection emails) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    /// <summary>The failure that dead-letters a message: one more than the retry steps.</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long a sent row (without bodies) stays: longer than the daily cap's window.</summary>
    public static readonly TimeSpan SentRetention = TimeSpan.FromHours(48);

    /// <summary>Of the hourly cap, what results-ready mail may use: a quarter (at least one) stays for account mail.</summary>
    public static int BulkCap(int hourlyCap) => Math.Max(1, hourlyCap - Math.Max(1, hourlyCap / 4));

    private readonly SemaphoreSlim running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Mail:BackgroundSender", true)) return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await ProcessDueAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Mail sender pass failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over due messages. Returns the number sent.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        await running.WaitAsync(ct);
        try
        {
            var mail = settings.Current.Mail;
            var now = time.GetUtcNow();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            // Before the transport check: mail queued while SMTP worked and never sent after it was removed must not keep
            // live links in the database for good (R2-ACC Info d).
            await ExpireUnsentAsync(db, now, ct);
            if (!mail.HasTransport) return 0; // queued mail still goes out while the Public URL is missing
            var sentBefore = now - SentRetention;
            await db.OutboxMessages.Where(m => m.SentAt != null && m.SentAt < sentBefore).ExecuteDeleteAsync(ct);

            var hourAgo = now - TimeSpan.FromHours(1);
            var sentLastHour = await db.OutboxMessages.CountAsync(m => m.SentAt != null && m.SentAt > hourAgo, ct);
            var budget = mail.HourlyCap - sentLastHour;
            if (budget <= 0) return 0;

            // Account and security mail (reset, confirmation, setup, notices) goes first; bulk results-ready mail gets what
            // is left, and never the last quarter of the hourly cap, so a burst of closes cannot hold a reset link back
            // until it has expired (R2-POLLS-3).
            var due = await db.OutboxMessages
                .Where(m => m.SentAt == null && m.DeadAt == null && m.SendAfter <= now && m.Type != MailType.ResultsReady)
                .OrderBy(m => m.SendAfter)
                .Take(budget)
                .ToListAsync(ct);
            var bulkBudget = Math.Min(budget - due.Count, BulkCap(mail.HourlyCap) - sentLastHour);
            if (bulkBudget > 0)
                due.AddRange(await db.OutboxMessages
                    .Where(m => m.SentAt == null && m.DeadAt == null && m.SendAfter <= now && m.Type == MailType.ResultsReady)
                    .OrderBy(m => m.SendAfter)
                    .Take(bulkBudget)
                    .ToListAsync(ct));

            var sent = 0;
            var password = settings.GetMailPassword();
            foreach (var message in due)
            {
                // Claimed before it is sent: SendAfter moves past ClaimTimeout and is saved first. A message whose outcome
                // cannot be saved after the send (shutdown, database locked or full, connection lost) is then not sent
                // again on the next pass but only once the claim runs out: at least once, never every 10 s. When the
                // claim itself cannot be saved, nothing is sent.
                message.SendAfter = time.GetUtcNow() + ClaimTimeout;
                if (!await TrySaveAsync(db, message, ct)) break;
                try
                {
                    // Recipient and bodies are stored encrypted (DESIGN.md §30). Unreadable ones (key ring lost) cannot be
                    // sent: the attempt fails and the message dead-letters like any other failure.
                    var to = emails.Reveal(message.To);
                    var text = emails.RevealBody(message.TextBody ?? "");
                    var html = message.HtmlBody is null ? null : emails.RevealBody(message.HtmlBody) ?? throw new UnreadableMailException();
                    if (to is null || text is null) throw new UnreadableMailException();
                    await transport.SendAsync(new OutgoingMail(to, message.Subject, text, html), mail, password, ct);
                    message.SentAt = time.GetUtcNow();
                    (message.TextBody, message.HtmlBody) = (null, null);
                    sent++;
                    metrics.MailsSent.Add(1);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    message.Attempts++;
                    metrics.MailsFailed.Add(1);
                    // A server's error text often quotes the recipient: never stored or logged (DESIGN.md §30).
                    message.LastError = Truncate(EmailProtection.WithoutAddresses($"{e.GetType().Name}: {e.Message}"), 1000);
                    if (message.Attempts >= MaxAttempts)
                    {
                        message.DeadAt = time.GetUtcNow();
                        (message.TextBody, message.HtmlBody) = (null, null); // LastError stays; nothing left to resend
                        logger.LogWarning("Mail {Id} ({Type}) dead after {Attempts} attempts: {Error}", message.Id, message.Type, message.Attempts, message.LastError);
                    }
                    else
                    {
                        message.SendAfter = time.GetUtcNow() + Backoff[message.Attempts - 1];
                        logger.LogInformation("Mail {Id} ({Type}) failed, attempt {Attempts}: {Error}", message.Id, message.Type, message.Attempts, message.LastError);
                    }
                }
                // The outcome is saved even while the host stops: the mail has gone (or failed) either way.
                if (!await TrySaveAsync(db, message, CancellationToken.None)) break;
            }
            return sent;
        }
        finally
        {
            running.Release();
        }
    }

    /// <summary>
    /// How long a claimed message is held back before another pass may send it: longer than one SMTP send can take. Only a
    /// message whose outcome could not be saved after the send waits this long.
    /// </summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Saves <paramref name="message"/>'s changes; on a database error logs it and ends the pass (false).</summary>
    private async Task<bool> TrySaveAsync(ApplicationDbContext db, OutboxMessage message, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception e) when (e is DbUpdateException || DatabaseErrors.IsDatabaseError(e))
        {
            logger.LogWarning("Mail {Id} ({Type}): saving the outbox failed ({Error}); the pass stops.", message.Id, message.Type, DatabaseErrors.Describe(e));
            return false;
        }
    }

    /// <summary>
    /// The longest a queued message waits: the longest lifetime of a link a mail can carry (the account setup link).
    /// Older unsent messages are dead-lettered without their bodies; their links would no longer work anyway.
    /// </summary>
    public static readonly TimeSpan UnsentLifetime = Accounts.AccountService.SetupLinkLifetime;

    private static Task<int> ExpireUnsentAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - UnsentLifetime;
        return db.OutboxMessages.Where(m => m.SentAt == null && m.DeadAt == null && m.CreatedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeadAt, now).SetProperty(m => m.TextBody, (string?)null)
                .SetProperty(m => m.HtmlBody, (string?)null).SetProperty(m => m.LastError, "Not sent in time; its links have expired."), ct);
    }

    public async Task<MailQueueStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var queued = await db.OutboxMessages.CountAsync(m => m.SentAt == null && m.DeadAt == null, ct);
        var dead = await db.OutboxMessages.CountAsync(m => m.DeadAt != null, ct);
        var last = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.LastError != null)
            .OrderByDescending(m => m.SendAfter)
            .Select(m => new { m.LastError, m.SendAfter, m.DeadAt })
            .FirstOrDefaultAsync(ct);
        return new MailQueueStats(queued, dead, last?.LastError, last?.DeadAt ?? last?.SendAfter);
    }

    /// <summary>Deletes dead messages. Returns how many.</summary>
    public async Task<int> PurgeDeadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OutboxMessages.Where(m => m.DeadAt != null && m.SentAt == null).ExecuteDeleteAsync(ct);
    }

    /// <summary>Last <paramref name="count"/> messages, newest first. No bodies.</summary>
    public async Task<IReadOnlyList<MailLogRow>> RecentAsync(int count = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.OutboxMessages.AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Take(count)
            .Select(m => new { m.To, m.Type, m.CreatedAt, m.SentAt, m.DeadAt, m.Attempts })
            .ToListAsync(ct);
        return rows.Select(m => new MailLogRow(emails.Display(m.To)!, m.Type,
            m.SentAt is not null ? "Sent" : m.DeadAt is not null ? "Dead" : m.Attempts > 0 ? $"Retrying ({m.Attempts})" : "Queued",
            m.SentAt ?? m.DeadAt ?? m.CreatedAt)).ToList();
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>A queued message whose recipient or body the key ring cannot decrypt.</summary>
public sealed class UnreadableMailException() : Exception("The recipient or the body cannot be decrypted with this key ring.");
