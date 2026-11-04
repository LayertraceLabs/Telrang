using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.EventLog;

namespace Telrang.Web.Hosting;

/// <summary>
/// Service manager integration (DESIGN.md §29): systemd (<c>Type=notify</c>, journal-formatted console logs) and the
/// Windows Service Control Manager (start/stop, Event Log). Both are no-ops when the process is not started by them.
/// </summary>
public static class ServiceHosting
{
    /// <summary>The Windows service name and Event Log source (install-service.ps1 registers both).</summary>
    public const string WindowsServiceName = "Telrang";

    /// <summary>Where the Windows service puts the admin setup/recovery key instead of the Event Log.</summary>
    public const string AdminKeyFileName = "admin-key.txt";

    public static void AddServiceHosting(this WebApplicationBuilder builder, HostPlatform platform)
    {
        builder.Services.AddSystemd();
        if (platform.IsSystemdService && !platform.InContainer && !Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
        {
            // What AddSystemd registers, for a unit whose parent it cannot see (ProtectProc=invisible, HostPlatform).
            builder.Logging.AddConsole(o => o.FormatterName = Microsoft.Extensions.Logging.Console.ConsoleFormatterNames.Systemd);
            builder.Services.AddSingleton<Microsoft.Extensions.Hosting.Systemd.ISystemdNotifier, Microsoft.Extensions.Hosting.Systemd.SystemdNotifier>();
            builder.Services.AddSingleton<IHostLifetime, Microsoft.Extensions.Hosting.Systemd.SystemdLifetime>();
        }
        builder.Services.AddWindowsService(o => o.ServiceName = WindowsServiceName);
        if (OperatingSystem.IsWindows()) builder.Services.Configure<EventLogSettings>(UseTelrangSource);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void UseTelrangSource(EventLogSettings settings)
    {
        settings.SourceName = WindowsServiceName;
        settings.LogName = "Application";
    }

    /// <summary>
    /// The key ring (<c>{DataDir}/keys</c>): persisted in the data directory; on Windows also encrypted with DPAPI, machine
    /// scope, so a copy of the folder is useless on another machine (DESIGN.md §29). Linux and Docker rely on the
    /// directory's permissions (0700, checked at startup).
    /// </summary>
    public static IDataProtectionBuilder ConfigureKeyRing(this IDataProtectionBuilder builder, string keysPath)
    {
        builder.SetApplicationName("Telrang").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        return builder;
    }

    /// <summary>
    /// A start that stops before serving: the message on stderr (journal, terminal) and, for the Windows service, in the
    /// Event Log, where an operator looks for a service that would not start. Messages never hold a secret.
    /// </summary>
    public static void ReportStartupError(HostPlatform platform, string message)
    {
        Console.Error.WriteLine("Telrang cannot start: " + message);
        if (!OperatingSystem.IsWindows() || !platform.IsWindowsService) return;
        try
        {
            if (EventLog.SourceExists(WindowsServiceName))
                EventLog.WriteEntry(WindowsServiceName, "Telrang cannot start: " + message, EventLogEntryType.Error);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            // No source and no right to create one: stderr is all there is.
        }
    }

    /// <summary>
    /// The Windows service writes the one-time admin key to <c>{DataDir}\admin-key.txt</c> (the data directory's ACL:
    /// SYSTEM, Administrators, the service account) instead of the Application Event Log, which every interactive user
    /// may read. Null elsewhere: the key goes to the console (journal, <c>docker logs</c>). A file left by an earlier start
    /// is deleted first.
    /// </summary>
    public static string? PrepareAdminKeyFile(HostPlatform platform, string dataDir)
    {
        var path = Path.Combine(dataDir, AdminKeyFileName);
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        // The systemd service too (pentest R3-DEP-12d): the journal keeps lines for its whole retention and members of
        // adm/systemd-journal read it; the file is in the service's private state directory.
        return platform.IsWindowsService || (platform.IsSystemdService && !platform.InContainer) ? path : null;
    }
}
