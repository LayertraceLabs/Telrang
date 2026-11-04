namespace Telrang.Web.Components.Layout;

/// <summary>
/// Fold-all state of the option help (DESIGN.md §22.2, §23.4) in one area (the admin pages, the poll editor), cascaded
/// by the component that owns the area's <c>HelpAllButton</c>. <see cref="SetAll"/> bumps <see cref="Generation"/>; each
/// <c>OptionHelp</c> adopts <see cref="AllOpen"/> once per generation, so a single toggle afterwards changes only itself.
/// </summary>
/// <param name="area">The area's name; the browser remembers the fold-all choice per area (<c>js/help.js</c>).</param>
public sealed class HelpState(string area)
{
    public const string Admin = "admin";
    public const string Editor = "editor";

    public string Area { get; } = area;

    public bool AllOpen { get; private set; }

    public int Generation { get; private set; }

    public void SetAll(bool open)
    {
        AllOpen = open;
        Generation++;
    }
}
