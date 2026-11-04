using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Telrang.Web.Hosting;

/// <summary>
/// Startup checks that the operator file and the data directory are closed to other local accounts (DESIGN.md §29: the
/// file may hold the database password; the data directory holds the database and the key ring). They warn, they do not
/// stop the start. Unix: mode bits. Windows: the ACL may grant access only to SYSTEM, Administrators and the account the
/// app runs as.
/// </summary>
public static class FilePermissions
{
    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// What is wrong with the operator file's permissions, or null. Unix: readable or writable by others, or writable by
    /// its group (the documented setup is <c>root:telrang 0640</c>: the service's group may read it).
    /// </summary>
    public static string? ConfigFileProblem(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new FileInfo(path).GetAccessControl();
                return WindowsOthers(security) is { Length: > 0 } others
                    ? $"The configuration file {path} is open to {others}; it may hold the database password. Allow only SYSTEM, Administrators and the service account."
                    : WindowsForeignOwner(security) is { } owner
                        ? $"The configuration file {path} is owned by {owner}, who can change its permissions. Make Administrators its owner (install-service.ps1 does)."
                        : null;
            }
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite)) != 0
                ? $"The configuration file {path} is readable or writable by other users (mode {Octal(mode)}); it may hold the database password. Run: chown root:telrang {path} && chmod 640 {path}"
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null; // read a moment ago; a race with a deletion is not worth a warning
        }
    }

    /// <summary>
    /// The operator file can be changed by the account the app runs as (pentest R3-DEP-10): a compromised app could then
    /// rewrite its own configuration (data directory, database, trusted proxies) for the next start. Null when it cannot.
    /// The check opens the file for writing without writing anything. Not reported for root/Administrators, who can write
    /// every file anyway (commands run from an admin shell).
    /// </summary>
    public static string? WritableBySelfProblem(string path)
    {
        if (RunsAsAdministrator()) return null;
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
            return OperatingSystem.IsWindows()
                ? $"The configuration file {path} can be changed by the account the app runs as. Give the service account read access only."
                : $"The configuration file {path} can be changed by the account the app runs as. Run: chown root:telrang {path} && chmod 640 {path}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What is wrong with the permissions of a file holding a secret (the connection string file, the Kestrel certificate,
    /// pentest R3-DEP-10), or null: as for the operator file, readable by others or writable by its group (Unix), or open
    /// to others than SYSTEM, Administrators and the service account, or owned by someone else (Windows).
    /// </summary>
    public static string? SecretFileProblem(string path, string what)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (OperatingSystem.IsWindows())
            {
                var security = new FileInfo(path).GetAccessControl();
                return WindowsOthers(security) is { Length: > 0 } others
                    ? $"{what} {path} is open to {others}. Allow only SYSTEM, Administrators and the service account."
                    : WindowsForeignOwner(security) is { } owner
                        ? $"{what} {path} is owned by {owner}, who can change its permissions. Make Administrators its owner."
                        : null;
            }
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite)) != 0
                ? $"{what} {path} is readable or writable by other users (mode {Octal(mode)}). Run: chown root:telrang {path} && chmod 640 {path}"
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The configured files that hold secrets: <c>ConnectionStrings:Default_FILE</c> and every Kestrel certificate file.</summary>
    public static IEnumerable<(string Path, string What)> SecretFiles(IConfiguration config)
    {
        if (config[Data.DatabaseSetup.ConnectionStringFileKey] is { Length: > 0 } csFile) yield return (csFile, "The connection string file");
        if (config["Kestrel:Certificates:Default:Path"] is { Length: > 0 } cert) yield return (cert, "The certificate file");
        foreach (var endpoint in config.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Certificate:Path"] is { Length: > 0 } endpointCert) yield return (endpointCert, "The certificate file");
        }
    }

    private static bool RunsAsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return Environment.UserName == "root";
        using var me = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator) || me.IsSystem;
    }

    /// <summary>
    /// What is wrong with the data directory's permissions, or null. Unix: the directory or its <c>keys</c> folder has any
    /// group or other bit (owner only, 0700, is what the app creates and the unit's <c>StateDirectoryMode</c> sets).
    /// </summary>
    public static string? DataDirProblem(string dataDir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The owner too (pentest R3-DEP-4): an owner keeps the right to change the ACL, so a folder another local
                // account created before the install stays theirs whatever its ACL says now.
                var security = new DirectoryInfo(dataDir).GetAccessControl();
                return WindowsOthers(security) is { Length: > 0 } others
                    ? $"The data directory {dataDir} is open to {others}. It holds the database and the key ring: allow only SYSTEM, Administrators and the service account."
                    : WindowsForeignOwner(security) is { } owner
                        ? $"The data directory {dataDir} is owned by {owner}, who can change its permissions. It holds the database and the key ring: run install-service.ps1 again (it takes ownership) or make Administrators its owner."
                        : null;
            }
            foreach (var dir in new[] { dataDir, Path.Combine(dataDir, "keys") })
            {
                if (!Directory.Exists(dir)) continue;
                var mode = File.GetUnixFileMode(dir);
                if ((mode & GroupOrOther) != 0)
                    return $"The data directory {dir} is open to other users (mode {Octal(mode)}). It holds the database and the key ring: chmod 700 {dir}";
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Creates a directory that only SYSTEM, Administrators and this account may open (Windows; inheritance off).</summary>
    [SupportedOSPlatform("windows")]
    public static void CreatePrivateWindowsDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in OwnSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
        new DirectoryInfo(path).Create(security);
    }

    /// <summary>SYSTEM, Administrators and the account this process runs as (a service's virtual account).</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> OwnSids()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (WindowsIdentity.GetCurrent().User is { } me) yield return me;
    }

    /// <summary>Names of the identities other than <see cref="OwnSids"/> that an allow rule grants any read or write right.</summary>
    [SupportedOSPlatform("windows")]
    private static string WindowsOthers(FileSystemSecurity security)
    {
        var own = OwnSids().ToHashSet();
        const FileSystemRights access = FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var others = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & access) == 0) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // applies to children only
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (own.Contains(sid) || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
            try { others.Add(sid.Translate(typeof(NTAccount)).Value); }
            catch (IdentityNotMappedException) { others.Add(sid.Value); }
        }
        return string.Join(", ", others);
    }

    /// <summary>The owner's name when it is not SYSTEM, Administrators or this account, else null.</summary>
    [SupportedOSPlatform("windows")]
    private static string? WindowsForeignOwner(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || OwnSids().Contains(owner)) return null;
        try { return owner.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return owner.Value; }
    }

    private static string Octal(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0b111_111_111, 8);
}
