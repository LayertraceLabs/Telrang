namespace Telrang.Web.Security;

/// <summary>
/// Bound from the <c>App</c> configuration section. <see cref="Name"/> is overridden by the DB setting
/// <c>App.DisplayName</c> when set (see SettingsService: IOptions&lt;AppOptions&gt; is served from there).
/// </summary>
public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>
    /// Fallback for the user-facing name: used only while the DB setting <c>App.DisplayName</c> (admin Settings tab) is
    /// empty. Not in appsettings.json: the name is renamed in one place, the admin page.
    /// </summary>
    public string Name { get; set; } = "Telrang";

    /// <summary>
    /// Directory holding app.db and the Data Protection key ring (keys/). Relative paths are
    /// resolved against the content root. Docker sets this to /data.
    /// </summary>
    public string DataDir { get; set; } = "./data";
}

public static class AppRoles
{
    public const string Admin = "Admin";
}

public static class AppPolicies
{
    public const string Admin = "Admin";

    /// <summary>Anyone except a signed-in account that still has to finish setup (see <see cref="AccountGate"/>).</summary>
    public const string NotGated = "NotGated";
}
