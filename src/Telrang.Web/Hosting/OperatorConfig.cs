using System.Text.Json;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace Telrang.Web.Hosting;

/// <summary>An operator configuration file the app cannot start with. The message names the file, never its content.</summary>
public sealed class OperatorConfigException(string message) : Exception(message);

/// <summary>The operator file that was loaded, and what is wrong with its permissions (null when nothing is).</summary>
public sealed record OperatorConfigFile(string Path, string? PermissionWarning);

/// <summary>
/// The operator-written JSON file (DESIGN.md §29): optional, outside the install folder, the same keys as the environment
/// (<c>Database:Provider</c>, <c>ConnectionStrings:Default</c>, <c>App:DataDir</c>, <c>Urls</c>, <c>Kestrel</c>, …).
/// Configuration sources, highest first: command line, environment variables, this file, the shipped
/// <c>appsettings*.json</c>. <c>TELRANG_CONFIG</c> names the file (<c>none</c>: no file); without it the platform default
/// (<see cref="HostPlatform.DefaultConfigFile"/>) is read when it exists. The app never writes the file.
/// </summary>
public static class OperatorConfig
{
    /// <summary>Environment variable (or command line <c>--TELRANG_CONFIG=</c>) naming the file.</summary>
    public const string PathKey = "TELRANG_CONFIG";

    /// <summary><c>TELRANG_CONFIG=none</c>: read no file, not even the default one.</summary>
    public const string None = "none";

    /// <summary>Larger files are refused: a settings file is a few hundred bytes.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>
    /// The file to read: the one <paramref name="named"/> (<c>TELRANG_CONFIG</c>) names, else the platform default; null
    /// for <c>none</c> or when the platform has no default (containers). <c>Named</c> is false for the default file, which
    /// may be missing.
    /// </summary>
    public static (string Path, bool Named)? Resolve(string? named, HostPlatform platform)
    {
        if (!string.IsNullOrWhiteSpace(named))
        {
            var value = named.Trim();
            return value.Equals(None, StringComparison.OrdinalIgnoreCase) ? null : (Path.GetFullPath(value), true);
        }
        return platform.DefaultConfigFile is { } path ? (path, false) : null;
    }

    /// <summary>
    /// Reads the file into configuration keys (<c>Section:Key</c>). Null when a default file does not exist. Throws
    /// <see cref="OperatorConfigException"/> when a named file is missing, or any file cannot be read or parsed: the app
    /// does not start with a configuration other than the one the operator wrote.
    /// </summary>
    public static Dictionary<string, string?>? Read(string path, bool named)
    {
        if (Directory.Exists(path))
            throw new OperatorConfigException($"The configuration file {path} is a directory.");
        if (!File.Exists(path))
        {
            if (named)
                throw new OperatorConfigException($"The configuration file {path} (TELRANG_CONFIG) does not exist. Set TELRANG_CONFIG=none to start without one.");
            // /etc/telrang is root:telrang 0750: for another account the file looks missing. Starting without the
            // service's configuration (another database, another data directory) is not what anyone wants.
            if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) && !CanList(folder))
                throw new OperatorConfigException(
                    $"The configuration folder {folder} cannot be read by this account. Run as the service account, or set TELRANG_CONFIG to another file or to none.");
            return null;
        }
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes)
                throw new OperatorConfigException($"The configuration file {path} is larger than 1 MB.");
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new OperatorConfigException(
                $"The configuration file {path} cannot be read by this account. Run as the service account, or set TELRANG_CONFIG to another file or to none.");
        }
        return Parse(bytes, path);
    }

    private static bool CanList(string folder)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Parses the file's bytes. Error messages give a position at most, never a character from the file.</summary>
    public static Dictionary<string, string?> Parse(byte[] bytes, string path)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            throw new OperatorConfigException($"The configuration file {path} is UTF-16; save it as UTF-8.");
        try
        {
            // The same leniency as the configuration's own JSON reader: comments and trailing commas are fine.
            using var document = JsonDocument.Parse(new MemoryStream(bytes),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new OperatorConfigException($"The configuration file {path} must hold one JSON object ({{ \"Section\": {{ \"Key\": \"value\" }} }}).");
        }
        catch (JsonException e)
        {
            // JsonException's message quotes the offending character; only its position is passed on.
            var line = e.LineNumber is { } l ? $" at line {l + 1}" : "";
            throw new OperatorConfigException($"The configuration file {path} is not valid JSON{line}.");
        }
        try
        {
            var root = new ConfigurationBuilder().AddJsonStream(new MemoryStream(bytes)).Build();
            return root.AsEnumerable()
                .Where(p => p.Value is not null)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            // Duplicate keys (case-insensitive) and the like; the reader's message may quote a value.
            throw new OperatorConfigException($"The configuration file {path} cannot be read as settings (a key appears twice?).");
        }
    }

    /// <summary>
    /// Puts <paramref name="values"/> below the environment variables and above the shipped <c>appsettings*.json</c>.
    /// The <c>ASPNETCORE_</c>-prefixed variables (<c>ASPNETCORE_URLS</c>, <c>ASPNETCORE_Kestrel__…</c>) are read once more
    /// right above the file, so they too beat it (ASP.NET Core reads them below <c>appsettings.json</c> otherwise).
    /// </summary>
    public static void Insert(IConfigurationBuilder config, IEnumerable<KeyValuePair<string, string?>> values)
    {
        var sources = config.Sources;
        var at = -1;
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
            {
                at = i;
                break;
            }
        }
        if (at < 0) throw new InvalidOperationException("No environment variables configuration source to insert the operator file below.");
        sources.Insert(at, new EnvironmentVariablesConfigurationSource { Prefix = "ASPNETCORE_" });
        sources.Insert(at, new MemoryConfigurationSource { InitialData = values.ToList() });
    }

    /// <summary>Resolves, reads and inserts the operator file. Null when none was read.</summary>
    public static OperatorConfigFile? Apply(IConfigurationManager config, HostPlatform platform)
    {
        if (Resolve(config[PathKey], platform) is not { } file) return null;
        if (Read(file.Path, file.Named) is not { } values) return null;
        Insert(config, values);
        return new OperatorConfigFile(file.Path, FilePermissions.ConfigFileProblem(file.Path) ?? FilePermissions.WritableBySelfProblem(file.Path));
    }
}
