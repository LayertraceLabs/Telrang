using Microsoft.EntityFrameworkCore;
using Telrang.Web.Security;

namespace Telrang.Web.Data;

/// <summary>
/// Startup step after the migrations (DESIGN.md §30): encrypts the email addresses a database written before §30 holds in
/// clear (accounts, outbox recipients and bodies, admin log notes) and fills the lookup hashes. Idempotent and resumable:
/// it picks rows by the missing <see cref="EmailProtection.Prefix"/> and rewrites each row in one statement guarded by its
/// old value, in batches, so a start interrupted half-way continues where it stopped. The same on both providers (EF only).
/// Then it checks that the stored addresses can be decrypted with this key ring and says so once when they cannot.
/// </summary>
public static class EmailUpgrade
{
    private const int BatchSize = 200;

    public sealed record Result(int Accounts, int OutboxMessages, int AdminLogNotes, int Unreadable);

    public static async Task<Result> RunAsync(ApplicationDbContext db, EmailProtection emails, ILogger logger, CancellationToken ct = default)
    {
        var accounts = await AccountsAsync(db, emails, ct);
        var outbox = await OutboxAsync(db, emails, ct);
        var notes = await NotesAsync(db, emails, ct);
        if (accounts + outbox + notes > 0)
            logger.LogInformation("Encrypted stored email addresses: {Accounts} accounts, {Outbox} queued mails, {Notes} admin log entries.",
                accounts, outbox, notes);

        var unreadable = 0;
        foreach (var stored in await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => u.Email).ToListAsync(ct))
        {
            if (emails.IsUnreadable(stored)) unreadable++;
        }
        if (unreadable > 0)
        {
            emails.UnreadableReported();
            logger.LogError("{Count} accounts have an email address this key ring cannot decrypt (the key directory was lost or replaced). " +
                "They show \"{Label}\" on the admin page and cannot sign in by email; restore {{DataDir}}/keys from a backup.",
                unreadable, EmailProtection.UnreadableLabel);
        }
        return new Result(accounts, outbox, notes, unreadable);
    }

    private static async Task<int> AccountsAsync(ApplicationDbContext db, EmailProtection emails, CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            var rows = await db.Users.AsNoTracking()
                .Where(u => u.UserName != null && !u.UserName.StartsWith(EmailProtection.Prefix))
                .OrderBy(u => u.Id).Take(BatchSize)
                .Select(u => new { u.Id, u.UserName, u.Email }).ToListAsync(ct);
            if (rows.Count == 0) return done;
            foreach (var row in rows)
            {
                var userName = row.UserName!;
                var email = emails.Reveal(row.Email); // plain before §30; an encrypted one (a row half-done by hand) is kept
                var protectedEmail = email is null ? row.Email : emails.Protect(email);
                var normalizedEmail = email is null ? null : emails.Hash(email);
                await db.Users.Where(u => u.Id == row.Id && u.UserName == userName).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.UserName, emails.Protect(userName))
                    .SetProperty(u => u.NormalizedUserName, emails.Hash(userName))
                    .SetProperty(u => u.Email, protectedEmail)
                    .SetProperty(u => u.NormalizedEmail, normalizedEmail), ct);
                done++;
            }
        }
    }

    private static async Task<int> OutboxAsync(ApplicationDbContext db, EmailProtection emails, CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            var rows = await db.OutboxMessages.AsNoTracking()
                .Where(m => !m.To.StartsWith(EmailProtection.Prefix))
                .OrderBy(m => m.Id).Take(BatchSize)
                .Select(m => new { m.Id, m.To, m.TextBody, m.HtmlBody, m.LastError }).ToListAsync(ct);
            if (rows.Count == 0) return done;
            foreach (var row in rows)
            {
                var to = row.To;
                var text = row.TextBody is null || EmailProtection.IsProtectedBody(row.TextBody) ? row.TextBody : emails.ProtectBody(row.TextBody);
                var html = row.HtmlBody is null || EmailProtection.IsProtectedBody(row.HtmlBody) ? row.HtmlBody : emails.ProtectBody(row.HtmlBody);
                var error = row.LastError is null ? null : EmailProtection.WithoutAddresses(row.LastError);
                await db.OutboxMessages.Where(m => m.Id == row.Id && m.To == to).ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.To, emails.Protect(to))
                    .SetProperty(m => m.ToHash, emails.Hash(to))
                    .SetProperty(m => m.TextBody, text)
                    .SetProperty(m => m.HtmlBody, html)
                    .SetProperty(m => m.LastError, error), ct);
                done++;
            }
        }
    }

    private static async Task<int> NotesAsync(ApplicationDbContext db, EmailProtection emails, CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            var rows = await db.AdminActionLogs.AsNoTracking()
                .Where(l => l.Note != null && !l.Note.StartsWith(EmailProtection.Prefix))
                .OrderBy(l => l.Id).Take(BatchSize)
                .Select(l => new { l.Id, l.Note }).ToListAsync(ct);
            if (rows.Count == 0) return done;
            foreach (var row in rows)
            {
                var note = row.Note!;
                await db.AdminActionLogs.Where(l => l.Id == row.Id && l.Note == note)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Note, emails.Protect(note)), ct);
                done++;
            }
        }
    }
}
