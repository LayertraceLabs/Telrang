using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Reports;

/// <summary>The report form's path (DESIGN.md §34.8): <c>/p/{id}/report</c>, for the form guard, its rate limit and its stylesheet.</summary>
public static partial class ReportRoutes
{
    [GeneratedRegex("^/p/[^/]+/report/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] // routing ignores case too
    private static partial Regex ReportPath();

    public static bool IsReportPath(PathString path) => path.Value is { Length: < 128 } v && ReportPath().IsMatch(v);

    public static string For(string encodedId) => $"p/{encodedId}/report";
}

public enum ReportStatus { Sent, Invalid, NotFound, Unavailable, Off }

public sealed record ReportResult(ReportStatus Status, IReadOnlyList<string> Errors)
{
    public static readonly ReportResult Sent = new(ReportStatus.Sent, []);
}

/// <summary>
/// Stores a visitor's report of a poll (DESIGN.md §34.8). The poll comes from the URL and is resolved through
/// <see cref="PollAccessService"/> (unknown = not found, disabled for this visitor = unavailable). Nothing about the reporter
/// is stored or passed in: no address, user agent, voter identity or account, only the category and what they typed, the
/// text and contact encrypted (DESIGN.md §30). Open reports are capped per poll and in all; a report over a cap is accepted
/// to the visitor and not stored, so the table cannot be flooded and the answer tells nothing. The first open report of a
/// poll mails the admins, at most once per <see cref="ReportMailGate.Interval"/>.
/// </summary>
public sealed class ReportService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PollAccessService access,
    EmailProtection emails,
    SettingsService settings,
    IMailQueue mail,
    ReportMailGate mailGate,
    TimeProvider time,
    ILogger<ReportService> logger)
{
    /// <summary>Open reports kept per poll; further ones are not stored.</summary>
    public const int MaxOpenPerPoll = 20;

    /// <summary>Open reports kept in all.</summary>
    public const int MaxOpen = 1000;

    public const string CategoryMessage = "Choose a category.";
    public static readonly string TextMessage = $"Shorten the details to {PollReport.MaxTextLength} characters.";
    public const string ContactMessage = "Enter an email address, or leave it empty.";

    private static readonly EmailAddressAttribute EmailFormat = new();

    public static readonly IReadOnlyList<(ReportCategory Category, string Label)> Categories =
    [
        (ReportCategory.IllegalContent, "Illegal content"),
        (ReportCategory.Abuse, "Abuse"),
        (ReportCategory.Impersonation, "Impersonation"),
        (ReportCategory.Spam, "Spam"),
        (ReportCategory.Other, "Other"),
    ];

    public static string Label(ReportCategory c) => Categories.First(x => x.Category == c).Label;

    /// <summary>The form's category value, or null for anything that is not one of <see cref="Categories"/>.</summary>
    public static ReportCategory? ParseCategory(string? value) =>
        Enum.TryParse<ReportCategory>(value, ignoreCase: false, out var c) && Enum.IsDefined(c) && value == c.ToString() ? c : null;

    /// <summary>Line breaks as one character each (browsers send CRLF), trimmed; null when empty.</summary>
    public static string? CleanText(string? text)
    {
        var t = text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    public async Task<ReportResult> SubmitAsync(string? encodedId, string? userId, ReportCategory? category, string? text, string? contact,
        CancellationToken ct = default)
    {
        if (!settings.Current.ReportsEnabled) return new ReportResult(ReportStatus.Off, []);
        text = CleanText(text);
        contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim();
        var errors = new List<string>();
        if (category is null) errors.Add(CategoryMessage);
        if (text is { Length: > PollReport.MaxTextLength }) errors.Add(TextMessage);
        if (contact is not null && (contact.Length > PollReport.MaxContactLength || !EmailFormat.IsValid(contact))) errors.Add(ContactMessage);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // The visitor's account decides only whether a disabled poll is theirs to see; it is not recorded.
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null) return new ReportResult(ReportStatus.NotFound, []);
        if (a.Unavailable) return new ReportResult(ReportStatus.Unavailable, []);
        if (errors.Count > 0) return new ReportResult(ReportStatus.Invalid, errors);

        bool first;
        // Count and insert as one step, so the caps hold under concurrent posts.
        await using (var tx = await db.Database.BeginWriteAsync(ct))
        {
            var pollId = a.Poll.Id;
            var openForPoll = await db.PollReports.CountAsync(r => r.PollId == pollId && r.State == ReportState.Open, ct);
            if (openForPoll >= MaxOpenPerPoll || await db.PollReports.CountAsync(r => r.State == ReportState.Open, ct) >= MaxOpen)
            {
                logger.LogInformation("Report not stored: the open reports are at their limit.");
                return ReportResult.Sent;
            }
            db.PollReports.Add(new PollReport
            {
                Id = Guid.NewGuid(),
                PollId = pollId,
                CreatedAt = time.GetUtcNow(),
                Category = category!.Value,
                TextCipher = text is null ? null : emails.Protect(text),
                ContactCipher = contact is null ? null : emails.Protect(contact),
                State = ReportState.Open,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            first = openForPoll == 0;
        }
        if (first) await MailAdminsAsync(db, ct);
        return ReportResult.Sent;
    }

    /// <summary>
    /// "A poll was reported" to every admin with a verified address (when mail is configured): no poll, title, text or contact,
    /// only the link to the Reports tab. At most one round per <see cref="ReportMailGate.Interval"/>, on top of the outbox's
    /// per-recipient limits, so a flood of reports is one mail.
    /// </summary>
    private async Task MailAdminsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (!settings.Current.Mail.IsConfigured || settings.MailLinkBase is not { } linkBase || !mailGate.TryTake()) return;
        var admins = await (from ur in db.UserRoles
                            join r in db.Roles on ur.RoleId equals r.Id
                            join u in db.Users on ur.UserId equals u.Id
                            where r.Name == AppRoles.Admin && u.DeletedAt == null && u.EmailConfirmed
                            select new { u.Id, u.Email }).ToListAsync(ct);
        var body = $"A poll on {settings.AppName} was reported.\n\nOpen reports: {linkBase}admin?tab=reports\n";
        foreach (var admin in admins)
        {
            if (emails.Reveal(admin.Email) is not { } address || !await AdminService.IsAdminInDatabaseAsync(db, admin.Id, ct)) continue;
            await mail.EnqueueAsync(address, "Poll reported", body, MailType.ReportReceived, ct: ct);
        }
    }
}

/// <summary>The admins' report mail at most once per <see cref="Interval"/>, process-wide (memory only).</summary>
public sealed class ReportMailGate(TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly Lock gate = new();
    private DateTimeOffset? last;

    public bool TryTake()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (last is { } l && now - l < Interval) return false;
            last = now;
            return true;
        }
    }
}
