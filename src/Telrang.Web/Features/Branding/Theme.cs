using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Telrang.Web.Features.Settings;

namespace Telrang.Web.Features.Branding;

public enum ThemePreset { System, Light, Dark }

public enum ThemeFont { SystemUi, Humanist, Serif, Mono }

/// <summary>App shell of the signed-in pages (MainLayout, DESIGN.md §11). Anonymous-facing pages always use PublicLayout.</summary>
public enum ThemeLayout { TopBar, Sidebar, Compact }

/// <summary>
/// The validated theme settings (DESIGN.md §17, §24.4). Stored values that fail validation (e.g. edited in the
/// database by hand) fall back to the defaults, so only validated values ever reach the stylesheet. One built-in palette
/// (Teal / Coral); the admin may override base and highlight per mode (null = the built-in colour).
/// </summary>
public sealed partial record ThemeSettings(
    ThemePreset Preset, int FontSize, ThemeFont Font, string? CustomCss, bool BackgroundEnabled, int BackgroundOverlay,
    ThemeLayout Layout = ThemeLayout.TopBar,
    string? LightBase = null, string? LightHighlight = null, string? DarkBase = null, string? DarkHighlight = null)
{
    /// <summary>Base and highlight of the built-in palette (Teal / Coral), for both modes.</summary>
    public const string DefaultBase = "#0f5e63";
    public const string DefaultHighlight = "#ff7a5c";
    /// <summary>Corner radius in px, fixed since §24.4 (no setting).</summary>
    public const int CornerRadius = 6;
    public const int DefaultFontSize = 16;
    public const int DefaultOverlay = 70;
    public const int MinFontSize = 14, MaxFontSize = 20, MinOverlay = 0, MaxOverlay = 90;
    public const int MaxCustomCssBytes = 20 * 1024;
    public const int MaxWelcomeBytes = 8 * 1024;

    public static readonly ThemeSettings Default = new(ThemePreset.System, DefaultFontSize, ThemeFont.SystemUi, null, false, DefaultOverlay);

    /// <summary>(value, label, CSS stack) of the fixed system font stacks; no web fonts, no font CDN.</summary>
    public static readonly IReadOnlyList<(ThemeFont Font, string Value, string Label, string Stack)> Fonts =
    [
        (ThemeFont.SystemUi, "system-ui", "System UI",
            "system-ui, -apple-system, \"Segoe UI\", Roboto, \"Helvetica Neue\", \"Noto Sans\", \"Liberation Sans\", Arial, sans-serif"),
        (ThemeFont.Humanist, "humanist", "Humanist",
            "Seravek, \"Gill Sans Nova\", Ubuntu, Calibri, \"DejaVu Sans\", source-sans-pro, sans-serif"),
        (ThemeFont.Serif, "serif", "Serif",
            "Charter, \"Bitstream Charter\", \"Sitka Text\", Cambria, Georgia, \"Times New Roman\", serif"),
        (ThemeFont.Mono, "mono", "Monospace",
            "ui-monospace, \"Cascadia Code\", \"Source Code Pro\", Menlo, Consolas, \"DejaVu Sans Mono\", monospace"),
    ];

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

    /// <summary>The four colour overrides: (key, label, built-in colour).</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string BuiltIn)> ColourKeys =
    [
        (SettingKeys.ThemeLightBase, "Light base", DefaultBase),
        (SettingKeys.ThemeLightHighlight, "Light highlight", DefaultHighlight),
        (SettingKeys.ThemeDarkBase, "Dark base", DefaultBase),
        (SettingKeys.ThemeDarkHighlight, "Dark highlight", DefaultHighlight),
    ];

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
    public string FontValue => Fonts.First(f => f.Font == Font).Value;
    public string FontStack => Fonts.First(f => f.Font == Font).Stack;

    /// <summary>
    /// The admin's default mode as the value of <c>data-bs-theme</c> on &lt;html&gt;: null for "system" (app.css follows
    /// prefers-color-scheme). A visitor's own choice wins over it (<see cref="ThemeService.BootstrapThemeFor"/>).
    /// </summary>
    public string? BootstrapTheme => Preset switch { ThemePreset.Light => "light", ThemePreset.Dark => "dark", _ => null };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexRegex();

    public static bool IsHex(string? value) => value is not null && HexRegex().IsMatch(value.Trim());

    public static bool TryPreset(string? value, out ThemePreset preset)
    {
        var match = Presets.FirstOrDefault(p => string.Equals(p.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        preset = match.Preset;
        return match.Value is not null;
    }

    public static bool TryFont(string? value, out ThemeFont font)
    {
        var match = Fonts.FirstOrDefault(f => string.Equals(f.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        font = match.Font;
        return match.Value is not null;
    }

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
    /// primary, accent, radius) may only be removed.
    /// </summary>
    public static string? Validate(IReadOnlyDictionary<string, string?> values)
    {
        string? V(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        if (V(SettingKeys.ThemePreset) is { } p && !TryPreset(p, out _)) return "Default mode: light, dark or system.";
        foreach (var (key, label, _) in ColourKeys)
        {
            if (V(key) is { } colour && !IsHex(colour)) return $"{label}: use #rrggbb.";
        }
        if (V(SettingKeys.LegacyThemePalette) is not null || V(SettingKeys.LegacyThemePrimary) is not null || V(SettingKeys.LegacyThemeAccent) is not null)
            return "Palette: set the colour overrides instead.";
        if (V(SettingKeys.LegacyThemeRadius) is not null) return $"Corner radius: fixed at {CornerRadius} px, leave it out.";
        if (V(SettingKeys.ThemeFontSize) is { } f && !TryInt(f, MinFontSize, MaxFontSize, out _)) return $"Font size: {MinFontSize} to {MaxFontSize} px.";
        if (V(SettingKeys.ThemeFontStack) is { } font && !TryFont(font, out _)) return "Font: choose one from the list.";
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
/// fixed template (custom properties consumed by app.css), then the admin's custom CSS verbatim. Colours come from
/// <see cref="PaletteDerivation"/>, each mode from its own base and highlight: a light block on <c>:root</c> (also
/// <c>data-bs-theme=light</c>), a dark block for <c>data-bs-theme=dark</c> (the visitor's choice or the admin's default
/// mode) and the same dark block for "system" on a dark OS.
/// </summary>
public static class ThemeCss
{
    /// <param name="includeCustomCss">False for the admin pages' variant (<c>?admin=1</c>).</param>
    public static string Generate(ThemeSettings t, string? backgroundUrl, bool includeCustomCss = true)
    {
        var light = PaletteDerivation.Derive(t.LightColours.Base, t.LightColours.Highlight, dark: false);
        var dark = PaletteDerivation.Derive(t.DarkColours.Base, t.DarkColours.Highlight, dark: true);
        var sb = new StringBuilder();
        sb.Append("/* Generated from the admin theme settings. Light: ").Append(t.LightColours.Base).Append(' ').Append(t.LightColours.Highlight)
          .Append(", dark: ").Append(t.DarkColours.Base).Append(' ').Append(t.DarkColours.Highlight).Append(". */\n:root {\n");
        Prop(sb, "--av-radius", ThemeSettings.CornerRadius.ToString(CultureInfo.InvariantCulture) + "px");
        Prop(sb, "--av-font", t.FontStack);
        foreach (var (name, value) in light.Tokens()) Prop(sb, name, value);
        sb.Append("}\n:root[data-bs-theme=dark] {\n");
        foreach (var (name, value) in dark.Tokens()) Prop(sb, name, value);
        sb.Append("}\n@media (prefers-color-scheme: dark) {\n  :root:not([data-bs-theme]) {\n");
        foreach (var (name, value) in dark.Tokens()) Prop(sb, name, value, "    ");
        sb.Append("  }\n}\n");
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
        var light = PaletteDerivation.Derive(t.LightColours.Base, t.LightColours.Highlight, dark: false);
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">"
            + $"<rect width=\"24\" height=\"24\" fill=\"{light.Primary.Hex}\"/>"
            + $"<g fill=\"{light.OnPrimary.Hex}\"><circle cx=\"19\" cy=\"6.5\" r=\"3.5\"/><circle cx=\"18\" cy=\"16.5\" r=\"4.5\"/><circle cx=\"8\" cy=\"19\" r=\"2.8\"/></g>"
            + $"<circle cx=\"9\" cy=\"9\" r=\"6.5\" fill=\"{light.Accent.Hex}\"/>"
            + "</svg>\n";
    }

    public static string Version(string css) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..16];

    private static void Prop(StringBuilder sb, string name, string value, string indent = "  ") =>
        sb.Append(indent).Append(name).Append(": ").Append(value).Append(";\n");
}
