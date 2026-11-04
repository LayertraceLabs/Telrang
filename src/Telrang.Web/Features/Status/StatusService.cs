using Telrang.Web.Data;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Status;

public sealed record RateRow(string Label, long LastHour, long LastDay);

public sealed record StatusSnapshot(
    DateTimeOffset StartedAt, TimeSpan Uptime, string Version, string Environment,
    string Database, long? DatabaseBytes, long? FreeBytes, string DataDir,
    int Users, int OpenPolls, int ClosedPolls, int Ballots,
    MailQueueStats Mail, bool MailConfigured, int Circuits, int Blocks,
    IReadOnlyList<RateRow> Rates, IReadOnlyList<string> Warnings, string? PublicUrl);

/// <summary>
/// Health and rates for the admin Status tab (DESIGN.md §14.8). Reads only aggregate counts and
/// in-memory counters; no per-user activity. Callers check the Admin policy (AdminService).
/// </summary>
public sealed class StatusService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AppStartInfo start,
    IWebHostEnvironment environment,
    SettingsService settings,
    MailSender mail,
    CircuitCounter circuits,
    AbuseTracker abuse,
    RateCounters rates,
    IConfiguration config,
    DatabaseSetup database,
    TimeProvider time)
{
    public async Task<StatusSnapshot> GetAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var polls = await db.Polls.AsNoTracking().Select(p => new { p.ClosedAt, p.ClosesAt }).ToListAsync(ct);
        var open = polls.Count(p => !Telrang.Web.Features.Polls.PollState.IsClosed(p.ClosedAt, p.ClosesAt, now));
        var current = settings.Current;

        var warnings = new List<string>();
        if (current.PublicUrl is null)
            warnings.Add("Public URL is empty: no mail is sent and share pages use the address the request came in on. Set it in Settings.");
        if (!current.Mail.IsConfigured)
            warnings.Add(current.RegistrationEnabled
                ? "Mail is not configured and registration is open: new accounts cannot verify their email or reset a password."
                : "Mail is not configured: no verification, reset or invite mails.");
        if (RecoveryKey.Requested(config))
            warnings.Add("App__RecoveryKey is set. Remove it and restart.");

        return new StatusSnapshot(
            start.StartedAt, now - start.StartedAt, AppVersion.Informational, environment.EnvironmentName,
            database.DisplayName, await DatabaseSizeAsync(db, ct), FreeSpace(start.DataDir), start.DataDir,
            await db.Users.CountAsync(u => u.DeletedAt == null, ct), open, polls.Count - open, await db.Ballots.CountAsync(ct),
            await mail.GetStatsAsync(ct), current.Mail.IsConfigured, circuits.Count, abuse.ActiveCount,
            AppMetrics.Rows.Select(r => new RateRow(r.Label, rates.Sum(r.Name, TimeSpan.FromHours(1)), rates.Sum(r.Name, TimeSpan.FromHours(24)))).ToList(),
            warnings, current.PublicUrl);
    }

    /// <summary>SQLite: the file with its WAL and shared memory; PostgreSQL: <c>pg_database_size</c>. Nothing else about the
    /// connection reaches the Status tab (DESIGN.md §28).</summary>
    private async Task<long?> DatabaseSizeAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (!database.IsPostgres) return DatabasePath(db) is { } path ? FileSizes(path) : null;
        try
        {
            return await db.Database.SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);
        }
        catch (Exception e) when (DatabaseErrors.IsDatabaseError(e))
        {
            return null; // e.g. a role without CONNECT on the catalog functions: "unknown"
        }
    }

    private static string? DatabasePath(ApplicationDbContext db)
    {
        try
        {
            var source = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
            return string.IsNullOrEmpty(source) || source == ":memory:" ? null : Path.GetFullPath(source);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The database file plus its WAL and shared-memory files.</summary>
    private static long? FileSizes(string path)
    {
        long total = 0;
        var any = false;
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            total += info.Length;
            any = true;
        }
        return any ? total : null;
    }

    /// <summary>Free space on the volume holding the data directory (the mount with the longest matching root).</summary>
    private static long? FreeSpace(string dataDir)
    {
        try
        {
            var full = Path.GetFullPath(dataDir);
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive?.AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Bytes(long? bytes) => bytes switch
    {
        null => "unknown",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : $"{Math.Max(0, (int)span.TotalMinutes)} min";
}
