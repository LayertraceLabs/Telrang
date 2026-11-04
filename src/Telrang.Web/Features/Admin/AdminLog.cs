using Telrang.Web.Data;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Admin;

public sealed record AdminLogRow(long Id, DateTimeOffset At, string AdminUserId, string? AdminEmail, string Action, string? TargetType, string? TargetId, string? Note);

/// <summary>Admin action log (DESIGN.md §14.8): who did what to which user/poll. Not a user activity log.</summary>
public static class AdminLog
{
    public const int MaxRows = 10_000;
    public const int PageSize = 50;

    /// <summary>
    /// Appends one entry and trims the table to <see cref="MaxRows"/> (oldest first). The note (it often names an account's
    /// address) is stored encrypted (DESIGN.md §30).
    /// </summary>
    public static async Task WriteAsync(ApplicationDbContext db, TimeProvider time, EmailProtection emails, string adminUserId, string action,
        string? targetType = null, string? targetId = null, string? note = null, CancellationToken ct = default)
    {
        db.AdminActionLogs.Add(new AdminActionLog
        {
            At = time.GetUtcNow(),
            AdminUserId = adminUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Note = note is null ? null : emails.Protect(note is { Length: > 500 } ? note[..500] : note),
        });
        await db.SaveChangesAsync(ct);

        var count = await db.AdminActionLogs.CountAsync(ct);
        if (count > MaxRows)
        {
            var cutoff = await db.AdminActionLogs.OrderByDescending(l => l.Id).Skip(MaxRows).Select(l => l.Id).FirstAsync(ct);
            await db.AdminActionLogs.Where(l => l.Id <= cutoff).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>Newest first; <paramref name="page"/> is 0-based.</summary>
    public static async Task<(IReadOnlyList<AdminLogRow> Rows, int Total)> PageAsync(ApplicationDbContext db, EmailProtection emails, int page,
        CancellationToken ct = default)
    {
        var total = await db.AdminActionLogs.CountAsync(ct);
        var rows = await (from l in db.AdminActionLogs.AsNoTracking()
                          join u in db.Users on l.AdminUserId equals u.Id into admins
                          from u in admins.DefaultIfEmpty()
                          orderby l.Id descending
                          select new AdminLogRow(l.Id, l.At, l.AdminUserId, u != null ? u.Email : null, l.Action, l.TargetType, l.TargetId, l.Note))
            .Skip(Math.Max(0, page) * PageSize).Take(PageSize).ToListAsync(ct);
        return (rows.Select(r => r with { AdminEmail = emails.Display(r.AdminEmail), Note = emails.Display(r.Note) }).ToList(), total);
    }
}
