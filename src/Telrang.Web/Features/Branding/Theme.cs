using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Telrang.Web.Features.Settings;

namespace Telrang.Web.Features.Branding;

public enum ThemePreset { System, Light, Dark }

public enum ThemeFont { SystemUi, Humanist, Serif, Mono }

/// <summary>App shell of the signed-in pages (MainLayout, DESIGN.md §11). Anonymous-facing pages always use PublicLayout.</summary>
public enum ThemeLayout { TopBar, Sidebar, Compact }

/// <summary>
/// The validated theme settings (DESIGN.md §17, §24.4). Stored values that fail validation (e.g. edited in the
/// database by hand) fall back to the defaults, so only validated values ever reach the stylesheet. One built-in palette
/// (Teal / Coral); the admin may override base and highlight per mode (null = the built-in colour). The colour, font and
/// radius part is <see cref="Look"/>, the type poll looks share (DESIGN.md §34.6): the same rules and the same CSS writer.
/// </summary>
public sealed record ThemeSettings(
    ThemePreset Preset, int FontSize, ThemeFont Font, string? CustomCss, bool BackgroundEnabled, int BackgroundOverlay,
    ThemeLayout Layout = ThemeLayout.TopBar,
    string? LightBase = null, string? LightHighlight = null, string? DarkBase = null, string? DarkHighlight = null)
{
    /// <summary>Base and highlight of the built-in palette (Teal / Coral), for both modes.</summary>
    public const string DefaultBase = ThemeLook.DefaultBase;
    public const string DefaultHighlight = ThemeLook.DefaultHighlight;
    /// <summary>Corner radius in px, fixed since §24.4 (no setting; a poll look has its own).</summary>
    public const int CornerRadius = ThemeLook.DefaultRadius;
    public const int DefaultFontSize = 16;
    public const int DefaultOverlay = 70;
    public const int MinFontSize = 14, MaxFontSize = 20, MinOverlay = 0, MaxOverlay = 90;
    public const int MaxCustomCssBytes = 20 * 1024;
    public const int MaxWelcomeBytes = 8 * 1024;

    public static readonly ThemeSettings Default = new(ThemePreset.System, DefaultFontSize, ThemeFont.SystemUi, null, false, DefaultOverlay);

    /// <summary>The fixed system font stacks (<see cref="ThemeLook.Fonts"/>).</summary>
    public static IReadOnlyList<(ThemeFont Font, string Value, string Label, string Stack)> Fonts => ThemeLook.Fonts;

    public static readonly IReadOnlyList<(ThemePreset Preset, string Value, string Label)> Presets =
    [
        (ThemePreset.System, "system", "System"),
        (ThemePreset.Light, "light", "Light"),
        (ThemePreset.Dark, "dark", "Dark"),
    ];

    public static readonly IReadOnlyList<(ThemeLayout Layout, string Value, string Label)> Layouts =
    [
        (ThemeLayout.TopBar, "topbar", "Top bar"),
        (ThemeLayout.Sidebar, "sidebar", "Sidebar"),
        (ThemeLayout.Compact, "compact", "Compact"),
    ];

    /// <summary>The four colour overrides: (key, label, built-in colour), in the order of <see cref="ThemeLook.ColourLabels"/>.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string BuiltIn)> ColourKeys =
        new[] { SettingKeys.ThemeLightBase, SettingKeys.ThemeLightHighlight, SettingKeys.ThemeDarkBase, SettingKeys.ThemeDarkHighlight }
            .Select((key, i) => (key, ThemeLook.ColourLabels[i].Label, ThemeLook.ColourLabels[i].BuiltIn)).ToList();

    /// <summary>Base and highlight the light mode is derived from: the overrides, else the built-in colours.</summary>
    public (string Base, string Highlight) LightColours => (LightBase ?? DefaultBase, LightHighlight ?? DefaultHighlight);

    /// <summary>Base and highlight the dark mode is derived from.</summary>
    public (string Base, string Highlight) DarkColours => (DarkBase ?? DefaultBase, DarkHighlight ?? DefaultHighlight);

    /// <summary>The stored override of one of <see cref="ColourKeys"/>, or null.</summary>
    public string? Override(string key) => key switch
    {
        SettingKeys.ThemeLightBase => LightBase,
        SettingKeys.ThemeLightHighlight => LightHighlight,
        SettingKeys.ThemeDarkBase => DarkBase,
        SettingKeys.ThemeDarkHighlight => DarkHighlight,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    public string LayoutValue => Layouts.First(l => l.Layout == Layout).Value;

    public string PresetValue => Presets.First(p => p.Preset == Preset).Value;
    public string FontValue => Look.FontValue;
    public string FontStack => Look.FontStack;

    /// <summary>
    /// The colour, font and radius part (DESIGN.md §34.6): the overrides or the built-in colours, the font, and the fixed
    /// <see cref="CornerRadius"/>. <c>theme.css</c> writes it with <see cref="ThemeCss.WriteLook"/>, as a poll's look.
    /// </summary>
    public ThemeLook Look => new(LightColours.Base, LightColours.Highlight, DarkColours.Base, DarkColours.Highlight, Font, CornerRadius);

    /// <summary>
    /// The admin's default mode as the value of <c>data-bs-theme</c> on &lt;html&gt;: null for "system" (app.css follows
    /// prefers-color-scheme). A visitor's own choice wins over it (<see cref="ThemeService.BootstrapThemeFor"/>).
    /// </summary>
    public string? BootstrapTheme => Preset switch { ThemePreset.Light => "light", ThemePreset.Dark => "dark", _ => null };

    public static bool IsHex(string? value) => ThemeLook.IsHex(value);

    public static bool TryPreset(string? value, out ThemePreset preset)
    {
        var match = Presets.FirstOrDefault(p => string.Equals(p.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        preset = match.Preset;
        return match.Value is not null;
    }

    public static bool TryFont(string? value, out ThemeFont font) => ThemeLook.TryFont(value, out font);

    public static bool TryLayout(string? value, out ThemeLayout layout)
    {
        var match = Layouts.FirstOrDefault(l => string.Equals(l.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        layout = match.Layout;
        return match.Value is not null;
    }

    private static bool TryInt(string? value, int min, int max, out int result) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= min && result <= max;

    /// <summary>
    /// Reads the settings; anything missing or invalid is the default. Upgrade of themes stored before §24.4: the colours of
    /// a Custom palette (or of a theme saved before palettes, with no palette row) are the override of both modes where no
    /// override is stored; a designed palette (Indigo / Amber, Plum / Gold) is dropped for the built-in colours; the radius
    /// is ignored. The next save of the Theme section removes those keys.
    /// </summary>
    public static ThemeSettings From(Func<string, string?> get)
    {
        var d = Default;
        string? Hex(string key) => get(key) is { } v && IsHex(v) ? v.Trim().ToLowerInvariant() : null;
        var legacyPalette = get(SettingKeys.LegacyThemePalette)?.Trim();
        var legacyCustom = string.IsNullOrEmpty(legacyPalette) || string.Equals(legacyPalette, "custom", StringComparison.OrdinalIgnoreCase);
        var (oldBase, oldHighlight) = legacyCustom ? (Hex(SettingKeys.LegacyThemePrimary), Hex(SettingKeys.LegacyThemeAccent)) : (null, null);
        return new ThemeSettings(
            TryPreset(get(SettingKeys.ThemePreset), out var preset) ? preset : d.Preset,
            TryInt(get(SettingKeys.ThemeFontSize), MinFontSize, MaxFontSize, out var size) ? size : d.FontSize,
            TryFont(get(SettingKeys.ThemeFontStack), out var font) ? font : d.Font,
            get(SettingKeys.ThemeCustomCss) is { Length: > 0 } css && Encoding.UTF8.GetByteCount(css) <= MaxCustomCssBytes ? css : null,
            bool.TryParse(get(SettingKeys.ThemeBackgroundEnabled), out var bg) && bg,
            TryInt(get(SettingKeys.ThemeBackgroundOverlay), MinOverlay, MaxOverlay, out var overlay) ? overlay : d.BackgroundOverlay,
            TryLayout(get(SettingKeys.ThemeLayout), out var layout) ? layout : d.Layout,
            Hex(SettingKeys.ThemeLightBase) ?? oldBase,
            Hex(SettingKeys.ThemeLightHighlight) ?? oldHighlight,
            Hex(SettingKeys.ThemeDarkBase) ?? oldBase,
            Hex(SettingKeys.ThemeDarkHighlight) ?? oldHighlight);
    }

    /// <summary>
    /// Checks the theme and welcome keys present in <paramref name="values"/> (blank = back to default, always fine).
    /// Returns the first error, or null. Used on every settings save (AdminService). The keys of before §24.4 (palette,
    /// primary, accent, radius) may only be removed. Colours and font go by <see cref="ThemeLook"/>'s rules, as a poll look's.
    /// </summary>
    public static string? Validate(IReadOnlyDictionary<string, string?> values)
    {
        string? V(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        if (V(SettingKeys.ThemePreset) is { } p && !TryPreset(p, out _)) return "Default mode: light, dark or system.";
        foreach (var (key, label, _) in ColourKeys)
        {
            if (V(key) is { } colour && ThemeLook.ColourProblem(label, colour) is { } problem) return problem;
        }
        if (V(SettingKeys.LegacyThemePalette) is not null || V(SettingKeys.LegacyThemePrimary) is not null || V(SettingKeys.LegacyThemeAccent) is not null)
            return "Palette: set the colour overrides instead.";
        if (V(SettingKeys.LegacyThemeRadius) is not null) return $"Corner radius: fixed at {CornerRadius} px, leave it out.";
        if (V(SettingKeys.ThemeFontSize) is { } f && !TryInt(f, MinFontSize, MaxFontSize, out _)) return $"Font size: {MinFontSize} to {MaxFontSize} px.";
        if (V(SettingKeys.ThemeFontStack) is { } font && ThemeLook.FontProblem(font) is { } fontProblem) return fontProblem;
        if (V(SettingKeys.ThemeLayout) is { } layout && !TryLayout(layout, out _)) return "Layout: choose one from the list.";
        if (V(SettingKeys.ThemeBackgroundEnabled) is { } b && !bool.TryParse(b, out _)) return "Background: true or false.";
        if (V(SettingKeys.ThemeBackgroundOverlay) is { } o && !TryInt(o, MinOverlay, MaxOverlay, out _)) return $"Overlay: {MinOverlay} to {MaxOverlay} %.";
        if (V(SettingKeys.ThemeCustomCss) is { } css && Encoding.UTF8.GetByteCount(css) > MaxCustomCssBytes) return "Custom CSS: 20 kB at most.";
        if (V(SettingKeys.HomeWelcomeMarkdown) is { } w && Encoding.UTF8.GetByteCount(w) > MaxWelcomeBytes) return "Welcome text: 8 kB at most.";
        return null;
    }
}

/// <summary>
/// Builds <c>/branding/theme.css</c> from <see cref="ThemeSettings"/>: validated values interpolated into a
/// fixed template (custom properties consumed by app.css), then the admin's custom CSS verbatim. The colour, font and radius
/// tokens are <see cref="WriteLook"/>, the one writer a poll's look (<c>/p/{id}/look.css</c>, DESIGN.md §34.6) goes through too.
/// </summary>
public static class ThemeCss
{
    /// <param name="includeCustomCss">False for the admin pages' variant (<c>?admin=1</c>).</param>
    public static string Generate(ThemeSettings t, string? backgroundUrl, bool includeCustomCss = true)
    {
        var sb = new StringBuilder();
        sb.Append("/* Generated from the admin theme settings. ");
        WriteLook(sb, t.Look);
        if (t.FontSize != ThemeSettings.DefaultFontSize)
        {
            // A percentage keeps the visitor's browser font-size preference in play.
            var percent = (t.FontSize * 100m / 16m).ToString("0.###", CultureInfo.InvariantCulture);
            sb.Append("html { font-size: ").Append(percent).Append("%; }\n");
        }
        if (t.BackgroundEnabled && backgroundUrl is not null)
        {
            var alpha = (t.BackgroundOverlay / 100m).ToString("0.##", CultureInfo.InvariantCulture);
            // The overlay is the page colour (light or dark), so text keeps its contrast.
            sb.Append("body {\n")
              .Append("  background-image: linear-gradient(rgba(var(--bs-body-bg-rgb), ").Append(alpha)
              .Append("), rgba(var(--bs-body-bg-rgb), ").Append(alpha).Append(")), url(\"").Append(backgroundUrl).Append("\");\n")
              .Append("  background-size: cover;\n  background-position: center;\n  background-attachment: fixed;\n}\n");
        }
        if (includeCustomCss && t.CustomCss is { Length: > 0 } css)
        {
            sb.Append("\n/* Custom CSS (admin) */\n").Append(css).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>/branding/favicon.svg</c>: the brand mark (<c>icons.svg#brand-mark</c>, same geometry) on the base colour: the three
    /// small dots in the base's text colour (white on a dark base, black on a pale one), the large dot in the highlight.
    /// Light-mode colours: a tab strip follows neither preset. The values are validated hex, so nothing is escaped.
    /// <c>wwwroot/favicon.png</c> is this on the default palette, for browsers without SVG icons.
    /// </summary>
    public static string Favicon(ThemeSettings t)
    {
        var light = PaletteDerivation.Derive(t.Look.LightBase, t.Look.LightHighlight, dark: false);
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">"
            + $"<rect width=\"24\" height=\"24\" fill=\"{light.Primary.Hex}\"/>"
            + $"<g fill=\"{light.OnPrimary.Hex}\"><circle cx=\"19\" cy=\"6.5\" r=\"3.5\"/><circle cx=\"18\" cy=\"16.5\" r=\"4.5\"/><circle cx=\"8\" cy=\"19\" r=\"2.8\"/></g>"
            + $"<circle cx=\"9\" cy=\"9\" r=\"6.5\" fill=\"{light.Accent.Hex}\"/>"
            + "</svg>\n";
    }

    /// <summary>
    /// The tokens of a look, after a comment that names its colours (the caller opens the comment): <c>--av-radius</c>,
    /// <c>--av-font</c> and every <see cref="ModeColors"/> token, each mode from its own base and highlight through
    /// <see cref="PaletteDerivation"/>: a light block on <c>:root</c> (also <c>data-bs-theme=light</c>), a dark block for
    /// <c>data-bs-theme=dark</c> (the visitor's choice or the admin's default mode) and the same dark block for "system" on a
    /// dark OS. Only custom properties; every value is a validated colour, a stack from the fixed list or whole pixels.
    /// </summary>
    public static void WriteLook(StringBuilder sb, ThemeLook look)
    {
        var (light, dark) = Modes(look);
        sb.Append("Light: ").Append(look.LightBase).Append(' ').Append(look.LightHighlight)
          .Append(", dark: ").Append(look.DarkBase).Append(' ').Append(look.DarkHighlight).Append(". */\n:root {\n");
        Prop(sb, "--av-radius", look.Radius.ToString(CultureInfo.InvariantCulture) + "px");
        Prop(sb, "--av-font", look.FontStack);
        foreach (var (name, value) in light.Tokens()) Prop(sb, name, value);
        sb.Append("}\n:root[data-bs-theme=dark] {\n");
        foreach (var (name, value) in dark.Tokens()) Prop(sb, name, value);
        sb.Append("}\n@media (prefers-color-scheme: dark) {\n  :root:not([data-bs-theme]) {\n");
        foreach (var (name, value) in dark.Tokens()) Prop(sb, name, value, "    ");
        sb.Append("  }\n}\n");
    }

    /// <summary><c>/p/{id}/look.css</c> (DESIGN.md §34.6): the poll's look as token overrides, nothing else.</summary>
    public static string PollLook(ThemeLook look)
    {
        var sb = new StringBuilder("/* Poll look. ");
        WriteLook(sb, look);
        return sb.ToString();
    }

    /// <summary>
    /// The look forms' preview (DESIGN.md §34.7): the same tokens scoped to the two sample panels instead of <c>:root</c>,
    /// <c>.look-preview-light</c> with the light mode's and <c>.look-preview-dark</c> with the dark mode's, so one page shows
    /// both. app.css maps Bootstrap's variables onto the tokens inside <c>.look-preview</c> too.
    /// </summary>
    public static string Preview(ThemeLook look)
    {
        var (light, dark) = Modes(look);
        var sb = new StringBuilder("/* Look preview. */\n.look-preview {\n");
        Prop(sb, "--av-radius", look.Radius.ToString(CultureInfo.InvariantCulture) + "px");
        Prop(sb, "--av-font", look.FontStack);
        sb.Append("}\n.look-preview-light {\n");
        foreach (var (name, value) in light.Tokens()) Prop(sb, name, value);
        sb.Append("}\n.look-preview-dark {\n");
        foreach (var (name, value) in dark.Tokens()) Prop(sb, name, value);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static (ModeColors Light, ModeColors Dark) Modes(ThemeLook look) =>
        (PaletteDerivation.Derive(look.LightBase, look.LightHighlight, dark: false), PaletteDerivation.Derive(look.DarkBase, look.DarkHighlight, dark: true));

    public static string Version(string css) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..16];

    private static void Prop(StringBuilder sb, string name, string value, string indent = "  ") =>
        sb.Append(indent).Append(name).Append(": ").Append(value).Append(";\n");
}
