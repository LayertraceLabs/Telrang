namespace Telrang.Web.Data;

/// <summary>
/// The data directory (database, key ring, branding, backup scratch). Created owner-only (0700) on Unix and with an ACL
/// for SYSTEM, Administrators and the app's account only on Windows, so other local users cannot read the database or
/// the keys whatever the umask (pentest AV-12). An existing directory keeps its permissions (it may be a volume the
/// operator set up); startup warns when they are open (<see cref="Hosting.FilePermissions.DataDirProblem"/>, DESIGN.md §29).
/// </summary>
public static class DataDirectory
{
    /// <summary>Scratch space for backups (<c>VACUUM INTO</c>), inside the data directory rather than the system temp.</summary>
    public static string TempPath(string dataDir) => Path.Combine(dataDir, "tmp");

    /// <summary>The Data Protection key ring.</summary>
    public static string KeysPath(string dataDir) => Path.Combine(dataDir, "keys");

    /// <summary>Creates the directory, its <c>keys</c> and <c>tmp</c> folders (private where created) and deletes backup leftovers.</summary>
    public static void Prepare(string dataDir)
    {
        CreatePrivate(dataDir);
        CreatePrivate(KeysPath(dataDir));
        var tmp = TempPath(dataDir);
        CreatePrivate(tmp);
        if (!OperatingSystem.IsWindows())
        {
            // The scratch folder holds full database copies: owner-only even when the data directory is shared.
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // A copy left behind by a process that died mid-download.
        foreach (var file in Directory.EnumerateFiles(tmp))
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void CreatePrivate(string path)
    {
        if (Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows()) Hosting.FilePermissions.CreatePrivateWindowsDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
