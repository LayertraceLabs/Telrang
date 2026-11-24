using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Telrang.Web.Features.Branding;

/// <summary>
/// The colour, font and corner-radius part of a theme (DESIGN.md §34.6): base and highlight for light and for dark mode
/// (every other colour is derived by <see cref="PaletteDerivation"/>), one of the fixed <see cref="Fonts"/> stacks and the
/// corner radius. The instance theme (<see cref="ThemeSettings.Look"/>) and a poll's look are this one type: one set of
/// rules (<see cref="Create"/>, used for the admin's theme fields, the look forms, stored looks and look codes alike) and one
/// CSS writer (<see cref="ThemeCss.WriteLook"/>). Every value is validated before it exists, so the stylesheets interpolate
/// nothing but <c>#rrggbb</c> colours, a font stack from the fixed list and a whole number of pixels.
/// </summary>
public sealed partial record ThemeLook(string LightBase, string LightHighlight, string DarkBase, string DarkHighlight, ThemeFont Font, int Radius)
{
    /// <summary>Base and highlight of the built-in palette (Teal / Coral), for both modes.</summary>
    public const string DefaultBase = "#0f5e63";
    public const string DefaultHighlight = "#ff7a5c";

    /// <summary>Corner radius of the instance theme (fixed since §24.4) and of a new look.</summary>
    public const int DefaultRadius = 6;
    public const int MinRadius = 0, MaxRadius = 16;

    /// <summary>The built-in colours, the System UI font and the default radius.</summary>
    public static readonly ThemeLook BuiltIn = new(DefaultBase, DefaultHighlight, DefaultBase, DefaultHighlight, ThemeFont.SystemUi, DefaultRadius);

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

    /// <summary>The four colours: (label, built-in colour). The labels are the error messages' field names.</summary>
    public static readonly IReadOnlyList<(string Label, string BuiltIn)> ColourLabels =
    [
        ("Light base", DefaultBase), ("Light highlight", DefaultHighlight), ("Dark base", DefaultBase), ("Dark highlight", DefaultHighlight),
    ];

    public (string Base, string Highlight) LightColours => (LightBase, LightHighlight);
    public (string Base, string Highlight) DarkColours => (DarkBase, DarkHighlight);

    public string FontValue => Fonts.First(f => f.Font == Font).Value;
    public string FontLabel => Fonts.First(f => f.Font == Font).Label;
    public string FontStack => Fonts.First(f => f.Font == Font).Stack;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexRegex();

    public static bool IsHex(string? value) => value is not null && HexRegex().IsMatch(value.Trim());

    public static bool TryFont(string? value, out ThemeFont font)
    {
        var match = Fonts.FirstOrDefault(f => string.Equals(f.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        font = match.Font;
        return match.Value is not null;
    }

    // ---- the one set of rules ----

    /// <summary>The refusal for a colour field, or null: <c>#rrggbb</c>.</summary>
    public static string? ColourProblem(string label, string? value) => IsHex(value) ? null : $"{label}: use #rrggbb.";

    public const string FontMessage = "Font: choose one from the list.";

    public static string? FontProblem(string? value) => TryFont(value, out _) ? null : FontMessage;

    public static string RadiusMessage => $"Corner radius: {MinRadius} to {MaxRadius} px.";

    public static string? RadiusProblem(int? radius) => radius is >= MinRadius and <= MaxRadius ? null : RadiusMessage;

    /// <summary>
    /// A look from its six fields, or the first refusal. The only way a look is made from outside values: the admin's theme
    /// fields (through <see cref="ThemeSettings.Validate"/>, which checks the same fields), the look forms, stored
    /// <c>LookJson</c> and look codes. Colours are trimmed and lower-cased.
    /// </summary>
    public static (ThemeLook? Look, string? Error) Create(string? lightBase, string? lightHighlight, string? darkBase, string? darkHighlight,
        string? font, int? radius)
    {
        string?[] colours = [lightBase, lightHighlight, darkBase, darkHighlight];
        for (var i = 0; i < colours.Length; i++)
        {
            if (ColourProblem(ColourLabels[i].Label, colours[i]) is { } problem) return (null, problem);
        }
        if (FontProblem(font) is { } fontProblem) return (null, fontProblem);
        if (RadiusProblem(radius) is { } radiusProblem) return (null, radiusProblem);
        TryFont(font, out var f);
        static string Hex(string? v) => v!.Trim().ToLowerInvariant();
        return (new ThemeLook(Hex(lightBase), Hex(lightHighlight), Hex(darkBase), Hex(darkHighlight), f, radius!.Value), null);
    }

    // ---- stored form: JSON ----

    /// <summary>The stored and exported form (<c>Poll.LookJson</c>, <c>SavedLook.LookJson</c>, look codes).</summary>
    private sealed class Stored
    {
        public string? LightBase { get; set; }
        public string? LightHighlight { get; set; }
        public string? DarkBase { get; set; }
        public string? DarkHighlight { get; set; }
        public string? Font { get; set; }
        public int? Radius { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        MaxDepth = 2,
    };

    /// <summary>Longest stored JSON read back; a look is about 150 bytes.</summary>
    public const int MaxJsonLength = 1024;

    public const string NotALookMessage = "Look code: not a look. Paste a code that starts with tl1.";

    public string ToJson() => JsonSerializer.Serialize(new Stored
    {
        LightBase = LightBase, LightHighlight = LightHighlight, DarkBase = DarkBase, DarkHighlight = DarkHighlight,
        Font = FontValue, Radius = Radius,
    }, Json);

    /// <summary>A look from its JSON, through <see cref="Create"/>; anything else is refused with one line.</summary>
    public static (ThemeLook? Look, string? Error) ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxJsonLength) return (null, NotALookMessage);
        Stored? s;
        try { s = JsonSerializer.Deserialize<Stored>(json, Json); }
        catch (JsonException) { return (null, NotALookMessage); }
        if (s is null) return (null, NotALookMessage);
        return Create(s.LightBase, s.LightHighlight, s.DarkBase, s.DarkHighlight, s.Font, s.Radius);
    }

    /// <summary>A stored look, or null (none, or a value that fails the rules: then the instance theme shows).</summary>
    public static ThemeLook? FromStored(string? json) => json is null ? null : ParseJson(json).Look;

    // ---- look codes (DESIGN.md §34.7) ----

    public const string CodePrefix = "tl1.";

    /// <summary>Longest look code accepted, in bytes.</summary>
    public const int MaxCodeBytes = 512;

    /// <summary><c>tl1.</c> + base64url of <see cref="ToJson"/>.</summary>
    public string ToCode() => CodePrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ToJson()));

    /// <summary>A look from a look code: the prefix, at most <see cref="MaxCodeBytes"/>, base64url, then <see cref="ParseJson"/>.</summary>
    public static (ThemeLook? Look, string? Error) FromCode(string? code)
    {
        var c = code?.Trim();
        if (c is null || !c.StartsWith(CodePrefix, StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(c) > MaxCodeBytes)
            return (null, NotALookMessage);
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(c.AsSpan(CodePrefix.Length)); }
        catch (FormatException) { return (null, NotALookMessage); }
        string json;
        try { json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return (null, NotALookMessage); }
        return ParseJson(json);
    }

    /// <summary>The radius for people: "6 px".</summary>
    public string RadiusText => Radius.ToString(CultureInfo.InvariantCulture) + " px";
}
