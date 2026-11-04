using Telrang.Web.Data;
using Telrang.Web.Features.Settings;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Mail;

public enum MailDropReason { None, NotConfigured, Cooldown, DailyMax }

/// <param name="RetryAfter">For <see cref="MailDropReason.Cooldown"/> and <see cref="MailDropReason.DailyMax"/>: when a message of this type to this recipient would be accepted again.</param>
public sealed record MailEnqueueResult(bool Queued, MailDropReason Reason = MailDropReason.None, TimeSpan? RetryAfter = null)
{
    public static readonly MailEnqueueResult Ok = new(true);

    /// <summary>
    /// The line to show a signed-in user about their own address ("Already sent recently. Try again in N minutes.").
    /// Never use it for addresses typed by anonymous visitors (no oracle).
    /// </summary>
    public string? DroppedMessage => Queued ? null : Reason switch
    {
        MailDropReason.Cooldown or MailDropReason.DailyMax when RetryAfter is { } r => $"Already sent recently. Try again in {Security.StaticPages.Minutes(r)}.",
        MailDropReason.NotConfigured => "Mail is not configured on this server.",
        _ => "Already sent recently. Try again later.",
    };
}

public interface IMailQueue
{
    /// <summary>
    /// Adds a message to the outbox, or drops it (logged at Information) when mail is not configured,
    /// the recipient got a message of the same type within the cooldown, or the recipient reached the
    /// daily maximum for that type. The result says which. Pages may show it only to a signed-in user
    /// about their own address; anonymous forms never branch on it, so they cannot become an oracle
    /// for "this address exists".
    /// </summary>
    Task<MailEnqueueResult> TryEnqueueAsync(string to, string subject, string textBody, MailType type, string? htmlBody = null, CancellationToken ct = default);

    /// <summary><see cref="TryEnqueueAsync"/> reduced to "was it queued".</summary>
    async Task<bool> EnqueueAsync(string to, string subject, string textBody, MailType type, string? htmlBody = null, CancellationToken ct = default) =>
        (await TryEnqueueAsync(to, subject, textBody, type, htmlBody, ct)).Queued;
}

/// <summary>Outbox writer (DESIGN.md §14.5). Limits are applied at enqueue time.</summary>
public sealed class MailQueue(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SettingsService settings,
    TimeProvider time,
    Security.EmailProtection emails,
    ILogger<MailQueue> logger) : IMailQueue
{
    /// <summary>Results-ready mails one recipient gets in 24 hours (R2-POLLS-3); further closes that day are not mailed.</summary>
    public const int ResultsReadyDailyMax = 20;

    public async Task<MailEnqueueResult> TryEnqueueAsync(string to, string subject, string textBody, MailType type, string? htmlBody = null, CancellationToken ct = default)
    {
        var mail = settings.Current.Mail;
        to = to.Trim();
        if (!mail.IsConfigured)
        {
            logger.LogInformation("Mail not configured; dropped {Type} message.", type);
            return new MailEnqueueResult(false, MailDropReason.NotConfigured);
        }

        var now = time.GetUtcNow();
        var recipient = emails.Hash(to); // the address is stored encrypted; limits count by its hash (DESIGN.md §30)
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The admin's own test mail and security notices are exempt from the per-recipient limits (still count toward the
        // hourly cap): a security notice follows a re-authenticated, rate-limited change of the account itself and must
        // not be swallowed by the cooldown of an earlier one. Results-ready mails have no cooldown (several polls can
        // close within one) but their own daily maximum (R2-POLLS-3). Both limits count messages of the same type only,
        // so nobody can use up a person's quota for reset mails by requesting verification mails.
        if (type is not (MailType.Test or MailType.SecurityNotice))
        {
            var results = type == MailType.ResultsReady;
            var cooldown = TimeSpan.FromMinutes(results ? 0 : mail.CooldownMinutes);
            var dailyMax = results ? ResultsReadyDailyMax : Math.Max(1, mail.DailyMax);
            var day = TimeSpan.FromDays(1);
            var dayStart = now - day;
            var recent = (await db.OutboxMessages.AsNoTracking()
                    .Where(m => m.ToHash == recipient && m.CreatedAt > dayStart && m.Type == type)
                    .Select(m => m.CreatedAt)
                    .ToListAsync(ct))
                .Order().ToList();
            if (cooldown > TimeSpan.Zero && recent.Count > 0 && recent[^1] > now - cooldown)
            {
                logger.LogInformation("Mail cooldown: dropped {Type} message.", type);
                return new MailEnqueueResult(false, MailDropReason.Cooldown, recent[^1] + cooldown - now);
            }
            if (recent.Count >= dailyMax)
            {
                logger.LogInformation("Mail daily maximum per recipient reached: dropped {Type} message.", type);
                return new MailEnqueueResult(false, MailDropReason.DailyMax, recent[recent.Count - dailyMax] + day - now);
            }
        }

        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            To = emails.Protect(to),
            ToHash = recipient,
            Subject = subject.Length > 200 ? subject[..200] : subject,
            // Encrypted until sent (pentest R3-DEP-6): a database copy taken while a mail waits holds no live link.
            TextBody = emails.ProtectBody(textBody),
            HtmlBody = htmlBody is null ? null : emails.ProtectBody(htmlBody),
            Type = type,
            CreatedAt = now,
            SendAfter = now,
        });
        await db.SaveChangesAsync(ct);
        return MailEnqueueResult.Ok;
    }
}
