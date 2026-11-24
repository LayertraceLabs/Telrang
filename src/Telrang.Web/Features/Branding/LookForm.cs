namespace Telrang.Web.Features.Branding;

/// <summary>
/// The fields of a look as a form holds them (<see cref="LookFields"/>): the admin's theme section, the poll editor's Look tab
/// and the looks page. A look exists only through <see cref="ToLook"/>, which applies <see cref="ThemeLook.Create"/>.
/// </summary>
public sealed class LookForm
{
    /// <summary>The colour fields' id suffixes, in the order of <see cref="ThemeLook.ColourLabels"/>.</summary>
    public static readonly IReadOnlyList<string> ColourIds = ["lightbase", "lighthighlight", "darkbase", "darkhighlight"];

    private readonly string[] colours = [ThemeLook.DefaultBase, ThemeLook.DefaultHighlight, ThemeLook.DefaultBase, ThemeLook.DefaultHighlight];

    public string Font { get; set; } = ThemeLook.BuiltIn.FontValue;
    public int Radius { get; set; } = ThemeLook.DefaultRadius;

    public string Colour(int i) => colours[i];

    public void SetColour(int i, string? value) => colours[i] = value?.Trim() ?? "";

    public static LookForm From(ThemeLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var f = new LookForm { Font = look.FontValue, Radius = look.Radius };
        (f.colours[0], f.colours[1], f.colours[2], f.colours[3]) = (look.LightBase, look.LightHighlight, look.DarkBase, look.DarkHighlight);
        return f;
    }

    public (ThemeLook? Look, string? Error) ToLook() => ThemeLook.Create(colours[0], colours[1], colours[2], colours[3], Font, Radius);
}
