using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Telrang.Web.Components.Layout;

/// <summary>
/// The routes of the pages that never run in a circuit (<c>[ExcludeFromInteractiveRouting]</c>: home, <c>/p/*</c>,
/// account, setup, notify), as one JavaScript regular expression (DESIGN.md §24.6). On an interactive page Blazor's
/// router intercepts every in-app link; for one of these pages the circuit then has to order a full reload, and the
/// browser unloads the page while that JS call is still in flight (<c>RemoteNavigationManager</c> "Navigation failed",
/// <c>CircuitHost</c> unhandled <c>TaskCanceledException</c>). <c>js/links.js</c> matches clicked links against this
/// pattern and lets the browser load them itself, before Blazor sees the click.
/// </summary>
public static partial class StaticRoutes
{
    /// <summary>The pattern for the path (no query), case-insensitive, anchored; e.g. <c>^(?:/p/[^/]+|/)$</c>.</summary>
    public static string Pattern { get; } = Build(typeof(Program).Assembly);

    public static string Build(Assembly assembly)
    {
        var templates = assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && t.GetCustomAttribute<ExcludeFromInteractiveRoutingAttribute>() is not null)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => r.Template))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(ToRegex);
        return "^(?:" + string.Join("|", templates) + ")/?$";
    }

    [GeneratedRegex(@"^\{(\*{1,2})?([^}:?=]+)(?::[^}]*)?(\?)?\}$")]
    private static partial Regex Parameter();

    /// <summary>One route template as a regex: literal segments escaped, <c>{x}</c> one segment, <c>{x?}</c> an optional
    /// one, <c>{*x}</c> the rest.</summary>
    private static string ToRegex(string template)
    {
        var path = new StringBuilder();
        foreach (var segment in template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Parameter().Match(segment) is { Success: true } p)
            {
                if (p.Groups[1].Success) path.Append("(?:/.*)?");
                else if (p.Groups[3].Success) path.Append("(?:/[^/]+)?");
                else path.Append("/[^/]+");
            }
            else path.Append('/').Append(Regex.Escape(segment).Replace("/", "\\/"));
        }
        return path.Length == 0 ? "" : path.ToString(); // "/" becomes "" and the trailing "/?" matches it
    }
}
