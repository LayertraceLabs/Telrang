using System.Reflection;

namespace Telrang.Web;

/// <summary>Version stamped at build time via -p:InformationalVersion (see scripts/publish-release.sh).</summary>
public static class AppVersion
{
    public static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Version without the "+commit" build metadata.</summary>
    public static readonly string Display = Informational.Split('+')[0];
}
