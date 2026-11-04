using System.Globalization;

namespace Telrang.Web.Features.Branding;

/// <summary>An sRGB colour with 8-bit channels; what ends up in the stylesheet as <c>#rrggbb</c>.</summary>
public readonly record struct Rgb(int R, int G, int B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    public static Rgb Parse(string hex) => new(
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    public string Hex => $"#{R:x2}{G:x2}{B:x2}";
    public string Triple => FormattableString.Invariant($"{R}, {G}, {B}");
    public string Rgba(double alpha) => FormattableString.Invariant($"rgba({R}, {G}, {B}, {alpha})");

    /// <summary>WCAG 2 relative luminance.</summary>
    public double Luminance
    {
        get
        {
            static double Lin(int c) { var v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    /// <summary>WCAG 2 contrast ratio (1 to 21).</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (x, y) = (a.Luminance, b.Luminance);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>White or black, whichever has more contrast on this colour.</summary>
    public Rgb ContrastText => Contrast(White, this) >= Contrast(Black, this) ? White : Black;
}

/// <summary>
/// OKLCH (Björn Ottosson's OKLab in polar form): L 0..1, C chroma, H hue in degrees. Lightness steps in OKLCH look even
/// across hues, so one set of lightness targets works for every palette. Out-of-gamut colours lose chroma (same L and H)
/// until they fit sRGB.
/// </summary>
public readonly record struct Oklch(double L, double C, double H)
{
    public static Oklch From(Rgb c)
    {
        static double Lin(int v) { var x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        var h = Math.Atan2(B, A) * 180 / Math.PI;
        return new Oklch(L, Math.Sqrt(A * A + B * B), h < 0 ? h + 360 : h);
    }

    public Oklch WithL(double l) => this with { L = Math.Clamp(l, 0, 1) };

    /// <summary>At most <paramref name="max"/> chroma.</summary>
    public Oklch CapC(double max) => this with { C = Math.Min(C, max) };

    public Rgb ToRgb()
    {
        var c = C;
        for (var i = 0; i < 400; i++, c = Math.Max(0, c - 0.002))
        {
            var (r, g, b) = Linear(L, c, H);
            const double eps = 1e-4;
            if ((r >= -eps && r <= 1 + eps && g >= -eps && g <= 1 + eps && b >= -eps && b <= 1 + eps) || c == 0)
                return new Rgb(Gamma(r), Gamma(g), Gamma(b));
        }
        var (r0, g0, b0) = Linear(L, 0, H);
        return new Rgb(Gamma(r0), Gamma(g0), Gamma(b0));
    }

    private static (double R, double G, double B) Linear(double L, double C, double H)
    {
        var a = C * Math.Cos(H * Math.PI / 180);
        var b = C * Math.Sin(H * Math.PI / 180);
        var l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static int Gamma(double v)
    {
        v = Math.Clamp(v, 0, 1);
        var g = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (int)Math.Round(g * 255);
    }
}

/// <summary>
/// Every colour of one mode (light or dark), derived from the base and highlight colours by <see cref="PaletteDerivation"/>.
/// <see cref="Tokens"/> lists them as the <c>--av-*</c> custom properties app.css reads.
/// </summary>
public sealed record ModeColors(
    Rgb Canvas, Rgb Surface, Rgb SurfaceElevated, Rgb SurfaceSubtle, Rgb SurfaceStrong, Rgb Border, Rgb BorderStrong,
    Rgb Text, Rgb TextStrong, Rgb TextMuted,
    Rgb Primary, Rgb PrimaryHover, Rgb PrimaryActive, Rgb OnPrimary, Rgb Link, Rgb LinkHover,
    Rgb PrimaryText, Rgb PrimarySubtle, Rgb PrimaryBorderSubtle, double ActiveAlpha,
    Rgb Accent, Rgb OnAccent, Rgb AccentText, Rgb AccentSubtle, Rgb AccentBorderSubtle, Rgb FocusOutline,
    Rgb SuccessFg, Rgb SuccessBg, Rgb SuccessBorder, Rgb WarningFg, Rgb WarningBg, Rgb WarningBorder,
    Rgb DangerFg, Rgb DangerBg, Rgb DangerBorder, Rgb NavStart, Rgb NavEnd)
{
    /// <summary>The token names, in the order <see cref="Tokens"/> emits them (the same for both modes).</summary>
    public static readonly IReadOnlyList<string> TokenNames = new ModeColors(
        default, default, default, default, default, default, default, default, default, default, default, default, default,
        default, default, default, default, default, default, 0, default, default, default, default, default, default, default,
        default, default, default, default, default, default, default, default, default, default).Tokens().Select(t => t.Name).ToList();

    public IEnumerable<(string Name, string Value)> Tokens()
    {
        yield return ("--av-canvas", Canvas.Hex);
        yield return ("--av-surface", Surface.Hex);
        yield return ("--av-surface-rgb", Surface.Triple);
        yield return ("--av-surface-elevated", SurfaceElevated.Hex);
        yield return ("--av-surface-subtle", SurfaceSubtle.Hex);
        yield return ("--av-surface-strong", SurfaceStrong.Hex);
        yield return ("--av-border", Border.Hex);
        yield return ("--av-border-strong", BorderStrong.Hex);
        yield return ("--av-text", Text.Hex);
        yield return ("--av-text-rgb", Text.Triple);
        yield return ("--av-text-strong", TextStrong.Hex);
        yield return ("--av-text-strong-rgb", TextStrong.Triple);
        yield return ("--av-text-muted", TextMuted.Hex);
        yield return ("--av-primary", Primary.Hex);
        yield return ("--av-primary-rgb", Primary.Triple);
        yield return ("--av-primary-hover", PrimaryHover.Hex);
        yield return ("--av-primary-active", PrimaryActive.Hex);
        yield return ("--av-on-primary", OnPrimary == Rgb.White ? "#fff" : OnPrimary == Rgb.Black ? "#000" : OnPrimary.Hex);
        yield return ("--av-link", Link.Hex);
        yield return ("--av-link-hover", LinkHover.Hex);
        yield return ("--av-primary-text", PrimaryText.Hex);
        yield return ("--av-primary-subtle", PrimarySubtle.Hex);
        yield return ("--av-primary-border-subtle", PrimaryBorderSubtle.Hex);
        yield return ("--av-active-bg", Primary.Rgba(ActiveAlpha));
        yield return ("--av-accent", Accent.Hex);
        yield return ("--av-accent-rgb", Accent.Triple);
        yield return ("--av-on-accent", OnAccent == Rgb.White ? "#fff" : OnAccent == Rgb.Black ? "#000" : OnAccent.Hex);
        yield return ("--av-accent-text", AccentText.Hex);
        yield return ("--av-accent-subtle", AccentSubtle.Hex);
        yield return ("--av-accent-border-subtle", AccentBorderSubtle.Hex);
        yield return ("--av-header-band", Accent.Hex);
        yield return ("--av-focus-outline", FocusOutline.Hex);
        yield return ("--av-focus-ring", Accent.Rgba(0.5));
        yield return ("--av-success-fg", SuccessFg.Hex);
        yield return ("--av-success-bg", SuccessBg.Hex);
        yield return ("--av-success-border", SuccessBorder.Hex);
        yield return ("--av-warning-fg", WarningFg.Hex);
        yield return ("--av-warning-bg", WarningBg.Hex);
        yield return ("--av-warning-border", WarningBorder.Hex);
        yield return ("--av-danger-fg", DangerFg.Hex);
        yield return ("--av-danger-bg", DangerBg.Hex);
        yield return ("--av-danger-border", DangerBorder.Hex);
        yield return ("--av-nav-start", NavStart.Hex);
        yield return ("--av-nav-end", NavEnd.Hex);
    }
}

/// <summary>
/// Derives every theme colour from two: the base (identity, primary buttons, brand, bars) and the highlight (sparingly:
/// header band, active indicators, focus, rank 1, the leading result). Deterministic, in OKLCH: fixed lightness targets
/// per mode with the base's hue as a faint tint, then contrast fixes (lightness steps until WCAG AA is met). The built-in
/// palette and the admin's per-mode overrides go through the same code.
/// </summary>
public static class PaletteDerivation
{
    public const double TextRatio = 4.5;
    public const double UiRatio = 3.0;

    public static ModeColors Derive(string baseHex, string highlightHex, bool dark) => Derive(Rgb.Parse(baseHex), Rgb.Parse(highlightHex), dark);

    public static ModeColors Derive(Rgb baseColour, Rgb highlight, bool dark)
    {
        var b = Oklch.From(baseColour);
        var a = Oklch.From(highlight);
        var h = b.H;
        Oklch N(double l, double c) => new(l, Math.Min(c, b.C), h); // neutral with a trace of the base hue

        return dark ? Dark(baseColour, highlight, b, a, N) : Light(baseColour, highlight, b, a, N);
    }

    private static ModeColors Light(Rgb baseColour, Rgb highlight, Oklch b, Oklch a, Func<double, double, Oklch> n)
    {
        var canvas = n(0.968, 0.006).ToRgb();
        var surface = n(0.995, 0.003).ToRgb();
        var subtle = n(0.958, 0.008).ToRgb();
        var strong = n(0.915, 0.012).ToRgb();
        Rgb[] backgrounds = [surface, canvas, subtle];
        var text = Ensure(n(0.25, 0.025), TextRatio, lighter: false, backgrounds);
        var textStrong = Ensure(n(0.18, 0.02), TextRatio, lighter: false, backgrounds);
        var muted = Ensure(n(0.50, 0.02), TextRatio, lighter: false, backgrounds);

        var primary = baseColour;
        var onPrimary = primary.ContrastText;
        var step = onPrimary == Rgb.White ? -1 : 1; // hover moves away from the text colour
        var link = Ensure(b, TextRatio, lighter: false, backgrounds);
        var primarySubtle = new Oklch(0.95, Math.Min(b.C, 0.03), b.H).ToRgb();
        var accentSubtle = new Oklch(0.955, Math.Min(a.C, 0.045), a.H).ToRgb();
        var (successFg, successBg, successBorder) = Status(145, b, dark: false);
        var (warningFg, warningBg, warningBorder) = Status(75, b, dark: false);
        var (dangerFg, dangerBg, dangerBorder) = Status(25, b, dark: false);
        return new ModeColors(
            canvas, surface, surface, subtle, strong, n(0.905, 0.01).ToRgb(), n(0.80, 0.015).ToRgb(),
            text, textStrong, muted,
            primary, b.WithL(b.L + step * 0.05).ToRgb(), b.WithL(b.L + step * 0.09).ToRgb(), onPrimary,
            link, Ensure(Oklch.From(link).WithL(Oklch.From(link).L - 0.08), TextRatio, lighter: false, backgrounds),
            Ensure(b, TextRatio, lighter: false, primarySubtle), primarySubtle, new Oklch(0.85, Math.Min(b.C, 0.06), b.H).ToRgb(), 0.1,
            highlight, highlight.ContrastText, Ensure(a, TextRatio, lighter: false, accentSubtle, surface), accentSubtle,
            new Oklch(0.84, Math.Min(a.C, 0.1), a.H).ToRgb(), Ensure(a, UiRatio, lighter: false, surface, canvas),
            successFg, successBg, successBorder, warningFg, warningBg, warningBorder, dangerFg, dangerBg, dangerBorder,
            NavStart(b), NavEnd(b));
    }

    private static ModeColors Dark(Rgb baseColour, Rgb highlight, Oklch b, Oklch a, Func<double, double, Oklch> n)
    {
        var canvas = n(0.165, 0.012).ToRgb();
        var surface = n(0.205, 0.016).ToRgb();
        var elevated = n(0.245, 0.018).ToRgb();
        var subtle = n(0.235, 0.018).ToRgb();
        var strong = n(0.30, 0.02).ToRgb();
        Rgb[] backgrounds = [surface, canvas, elevated, subtle];
        var text = Ensure(n(0.91, 0.012), TextRatio, lighter: true, backgrounds);       // never pure white
        var textStrong = Ensure(n(0.96, 0.008), TextRatio, lighter: true, backgrounds);
        var muted = Ensure(n(0.74, 0.015), TextRatio, lighter: true, backgrounds);

        // Base lifted for the dark page: as light as it can get while white text on it keeps AA.
        var primary = baseColour;
        if (Rgb.Contrast(Rgb.White, baseColour) >= TextRatio)
        {
            for (var l = b.L; l <= 0.62; l += 0.005)
            {
                var c = b.WithL(l).ToRgb();
                if (Rgb.Contrast(Rgb.White, c) < TextRatio + 0.1) break;
                primary = c;
            }
        }
        var p = Oklch.From(primary);
        var onPrimary = primary.ContrastText;
        var step = onPrimary == Rgb.White ? -1 : 1;
        var link = Ensure(new Oklch(Math.Max(0.80, b.L), Math.Min(b.C, 0.13), b.H), TextRatio, lighter: true, backgrounds);
        var primarySubtle = new Oklch(0.28, Math.Min(b.C, 0.05), b.H).ToRgb();
        var accent = a.L < 0.8 ? a.WithL(0.8).ToRgb() : highlight;
        var accentSubtle = new Oklch(0.27, Math.Min(a.C, 0.05), a.H).ToRgb();
        var (successFg, successBg, successBorder) = Status(145, b, dark: true);
        var (warningFg, warningBg, warningBorder) = Status(75, b, dark: true);
        var (dangerFg, dangerBg, dangerBorder) = Status(25, b, dark: true);
        return new ModeColors(
            canvas, surface, elevated, subtle, strong, n(0.33, 0.02).ToRgb(), n(0.46, 0.025).ToRgb(),
            text, textStrong, muted,
            primary, p.WithL(p.L + step * 0.04).ToRgb(), p.WithL(p.L + step * 0.08).ToRgb(), onPrimary,
            link, Ensure(new Oklch(0.9, Math.Min(b.C, 0.07), b.H), TextRatio, lighter: true, backgrounds),
            Ensure(Oklch.From(link), TextRatio, lighter: true, primarySubtle), primarySubtle, new Oklch(0.42, Math.Min(b.C, 0.09), b.H).ToRgb(), 0.22,
            accent, accent.ContrastText, Ensure(Oklch.From(accent), TextRatio, lighter: true, accentSubtle, surface), accentSubtle,
            new Oklch(0.45, Math.Min(a.C, 0.1), a.H).ToRgb(), Ensure(Oklch.From(accent), UiRatio, lighter: true, surface, canvas, elevated),
            successFg, successBg, successBorder, warningFg, warningBg, warningBorder, dangerFg, dangerBg, dangerBorder,
            NavStart(b), NavEnd(b));
    }

    /// <summary>Status colours (success 145°, warning 75°, danger 25°) nudged 12 % toward the base hue, so they sit with it.</summary>
    private static (Rgb Fg, Rgb Bg, Rgb Border) Status(double hue, Oklch b, bool dark)
    {
        var h = b.C < 0.02 ? hue : Toward(hue, b.H, 0.12);
        if (dark)
        {
            var bg = new Oklch(0.26, 0.045, h).ToRgb();
            return (Ensure(new Oklch(0.82, 0.11, h), TextRatio, lighter: true, bg), bg, new Oklch(0.42, 0.08, h).ToRgb());
        }
        var lightBg = new Oklch(0.955, 0.035, h).ToRgb();
        return (Ensure(new Oklch(0.46, 0.13, h), TextRatio, lighter: false, lightBg, Rgb.White), lightBg, new Oklch(0.85, 0.07, h).ToRgb());
    }

    private static Rgb NavStart(Oklch b) => new Oklch(0.27, Math.Min(b.C, 0.11), b.H).ToRgb();
    private static Rgb NavEnd(Oklch b) => new Oklch(0.2, Math.Min(b.C, 0.08), b.H + 12).ToRgb();

    private static double Toward(double from, double to, double t)
    {
        var d = ((to - from) % 360 + 540) % 360 - 180; // shortest signed difference
        return (from + d * t + 360) % 360;
    }

    /// <summary>
    /// <paramref name="c"/>, stepped lighter or darker in OKLCH lightness until its contrast with every background is at
    /// least <paramref name="ratio"/>; white or black when no step gets there.
    /// </summary>
    public static Rgb Ensure(Oklch c, double ratio, bool lighter, params Rgb[] backgrounds)
    {
        for (var i = 0; i < 220; i++)
        {
            var rgb = c.ToRgb();
            if (backgrounds.All(bg => Rgb.Contrast(rgb, bg) >= ratio)) return rgb;
            if (c.L is <= 0 or >= 1) break;
            c = c.WithL(c.L + (lighter ? 0.005 : -0.005));
        }
        return lighter ? Rgb.White : Rgb.Black;
    }
}
