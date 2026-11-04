using Telrang.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Security;

/// <summary>
/// Startup: ensures the Admin role exists and, while no (non-deleted) admin exists, issues the
/// one-time setup key for <c>/setup</c> and logs it once at Warning (DESIGN.md §14.1).
/// </summary>
public sealed class AdminBootstrapper(
    ApplicationDbContext db,
    RoleManager<IdentityRole> roleManager,
    SetupKey setupKey,
    RecoveryKey recoveryKey,
    IConfiguration config,
    ILogger<AdminBootstrapper> logger)
{
    /// <summary>The keys issued at this start, logged by Program once the server has its addresses (so the log can name the URL).</summary>
    public sealed record Issued(string? SetupKey, string? RecoveryKey);

    public async Task<Issued> SeedAsync()
    {
        string? setup = null, recovery = null;
        if (!await roleManager.RoleExistsAsync(AppRoles.Admin))
        {
            var created = await roleManager.CreateAsync(new IdentityRole(AppRoles.Admin));
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    "Could not create Admin role: " + string.Join(", ", created.Errors.Select(e => e.Description)));
            }
        }

        if (!await AdminExistsAsync(db))
        {
            setup = setupKey.Issue();
        }
        else if (RecoveryKey.Requested(config))
        {
            recovery = recoveryKey.Issue();
        }
        else
        {
            logger.LogInformation("An admin account exists; no setup key issued. Lost access: start with App__RecoveryKey=1.");
        }
        if (RecoveryKey.Requested(config))
        {
            logger.LogWarning("App__RecoveryKey (App:IssueRecoveryKey) is set. Remove it once the admin account is recovered.");
        }
        return new Issued(setup, recovery);
    }

    /// <summary>
    /// Logs the issued keys with the page to open: the configured public URL, else the first listening address
    /// (https first; a wildcard host is shown as a placeholder), else the path alone.
    /// </summary>
    /// <param name="keyFile">The Windows service's key file (DESIGN.md §29): the lines go there instead of the log, and the
    /// log says where to find them. Null: the log (console, journal, <c>docker logs</c>).</param>
    public static void LogIssued(Issued issued, ILogger logger, string? publicUrl, IEnumerable<string> addresses, string? keyFile = null)
    {
        if (issued.SetupKey is null && issued.RecoveryKey is null) return;
        if (keyFile is not null)
        {
            var lines = new List<string>();
            var fileLogger = new LineLogger(lines);
            LogIssued(issued, fileLogger, publicUrl, addresses);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600
            using (var writer = new StreamWriter(keyFile, options))
            {
                foreach (var line in lines) writer.WriteLine(line);
            }
            logger.LogWarning("Admin {Kind} key issued (valid 1 h): read it from {File} (the service account and administrators only).",
                issued.SetupKey is not null ? "setup" : "recovery", keyFile);
            return;
        }
        var address = addresses.Where(u => ReverseProxy.UnixSocketPath(u) is null)
            .OrderByDescending(u => u.StartsWith("https", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        var baseUrl = (publicUrl ?? address ?? "").TrimEnd('/')
            .Replace("://+", "://<host>").Replace("://*", "://<host>").Replace("://0.0.0.0", "://<host>").Replace("://[::]", "://<host>");
        if (issued.SetupKey is { } setup)
        {
            logger.LogWarning(SecretLogEvents.SetupKey, "Admin setup key: {SetupKey} (valid 1 h). Open {Url} and enter it.", setup, baseUrl + "/setup");
        }
        if (issued.RecoveryKey is { } recovery)
        {
            logger.LogWarning(SecretLogEvents.RecoveryKey, "Admin recovery key: {RecoveryKey} (valid 1 h). Open {Url} and enter it; remove App__RecoveryKey and restart afterwards.", recovery, baseUrl + "/setup/recover");
        }
    }

    public static Task<bool> AdminExistsAsync(ApplicationDbContext db, CancellationToken ct = default) =>
        (from ur in db.UserRoles
         join r in db.Roles on ur.RoleId equals r.Id
         join u in db.Users on ur.UserId equals u.Id
         where r.Name == AppRoles.Admin && u.DeletedAt == null
         select u.Id).AnyAsync(ct);
}

/// <summary>Collects formatted log lines (the Windows service's key file).</summary>
file sealed class LineLogger(List<string> lines) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        lines.Add(formatter(state, exception));
}

/// <summary>
/// Event ids of log lines that carry a secret (the setup and recovery keys). They go to the console only: the
/// in-memory <c>LogRing</c> behind the admin Status tab drops them, so a key never shows in the browser.
/// </summary>
public static class SecretLogEvents
{
    public static readonly EventId SetupKey = new(9001, "AdminSetupKey");
    public static readonly EventId RecoveryKey = new(9002, "AdminRecoveryKey");

    public static bool IsSecret(EventId id) => id.Id == SetupKey.Id || id.Id == RecoveryKey.Id;
}
