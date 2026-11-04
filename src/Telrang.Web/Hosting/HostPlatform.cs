using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Telrang.Web.Hosting;

/// <summary>
/// Where and how the process runs (DESIGN.md §29): Windows service, systemd service, container or a plain process. It
/// decides the default operator configuration file and the default data directory; everything else is the same
/// program with the same configuration rules in all three install formats.
/// </summary>
/// <param name="IsWindows">The OS is Windows.</param>
/// <param name="IsWindowsService">Started by the Service Control Manager.</param>
/// <param name="IsSystemdService">Started by systemd (system or user manager).</param>
/// <param name="InContainer">The .NET container images set <c>DOTNET_RUNNING_IN_CONTAINER=true</c>.</param>
/// <param name="ProgramData"><c>%ProgramData%</c> (Windows), else null.</param>
/// <param name="StateDirectory">systemd's <c>$STATE_DIRECTORY</c> (set by <c>StateDirectory=</c>), else null.</param>
public sealed record HostPlatform(bool IsWindows, bool IsWindowsService, bool IsSystemdService, bool InContainer,
    string? ProgramData, string? StateDirectory)
{
    /// <summary>The default operator file on Linux.</summary>
    public const string LinuxConfigFile = "/etc/telrang/telrang.json";

    /// <summary>The default data directory of the systemd service when <c>$STATE_DIRECTORY</c> is not set.</summary>
    public const string LinuxStateDirectory = "/var/lib/telrang";

    public static HostPlatform Current()
    {
        var isWindows = OperatingSystem.IsWindows();
        return new HostPlatform(
            IsWindows: isWindows,
            IsWindowsService: isWindows && WindowsServiceHelpers.IsWindowsService(),
            IsSystemdService: !isWindows && (SystemdHelpers.IsSystemdService() || StartedByUnit()),
            InContainer: string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
            ProgramData: isWindows ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) : null,
            StateDirectory: Environment.GetEnvironmentVariable("STATE_DIRECTORY"));
    }

    /// <summary>
    /// systemd sets <c>INVOCATION_ID</c> for every unit and <c>NOTIFY_SOCKET</c> for <c>Type=notify</c>.
    /// <see cref="SystemdHelpers.IsSystemdService"/> looks at the parent's <c>/proc/1/comm</c>, which
    /// <c>ProtectProc=invisible</c> hides; these two variables still tell.
    /// </summary>
    private static bool StartedByUnit() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTIFY_SOCKET"));

    /// <summary>Runs under a service manager: the content root is the install folder, not the working directory.</summary>
    public bool IsService => IsWindowsService || (IsSystemdService && !InContainer);

    /// <summary><c>%ProgramData%\Telrang</c> on Windows (config file and data live below it), else null.</summary>
    public string? WindowsHome => IsWindows && !string.IsNullOrEmpty(ProgramData) ? Path.Combine(ProgramData, "Telrang") : null;

    /// <summary>
    /// The operator file read when <c>TELRANG_CONFIG</c> is not set: <c>/etc/telrang/telrang.json</c> on Linux,
    /// <c>%ProgramData%\Telrang\telrang.json</c> on Windows, none in a container (environment only). The same for a
    /// service and for a command run from a shell, so <c>copy-to-postgres</c> sees the service's configuration.
    /// </summary>
    public string? DefaultConfigFile =>
        InContainer ? null
        : IsWindows ? (WindowsHome is { } home ? Path.Combine(home, "telrang.json") : null)
        : LinuxConfigFile;

    /// <summary>
    /// The data directory when <c>App:DataDir</c> is not set: <c>%ProgramData%\Telrang\data</c> for the Windows service,
    /// systemd's state directory (<c>/var/lib/telrang</c>) for the systemd service, else <c>./data</c> under the content root.
    /// </summary>
    public string DefaultDataDir(string contentRoot)
    {
        if (IsWindowsService && WindowsHome is { } home) return Path.Combine(home, "data");
        if (IsSystemdService && !InContainer)
        {
            // StateDirectory= may list several directories, separated by ':'; Telrang's unit lists one.
            var state = StateDirectory?.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(state) ? LinuxStateDirectory : state;
        }
        return Path.GetFullPath("data", contentRoot);
    }
}
