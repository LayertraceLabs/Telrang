using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Theme colours (DESIGN.md §11, §17, §24.4): the built-in palette, per-mode overrides, WCAG AA contrast, theme.css, the
/// upgrade of themes stored with palettes, and the visitor's light/dark cookie.
/// </summary>
public partial class PaletteTests(ITestOutputHelper output)
{
    private static ThemeSettings Read(params (string Key, string Value)[] rows) =>
        ThemeSettings.From(key => rows.Where(r => r.Key == key).Select(r => r.Value).FirstOrDefault());

    /// <summary>The three blocks of the generated sheet: light (:root), fixed dark, system dark.</summary>
    private static (string Light, string Dark, string SystemDark) Blocks(string css)
    {
        var light = Regex.Match(css, @"^:root \{\n(.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
        var dark = Regex.Match(css, @"^:root\[data-bs-theme=dark\] \{\n(.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
        var system = Regex.Match(css, @"^  :root:not\(\[data-bs-theme\]\) \{\n(.*?)^  \}", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
        return (light, dark, system);
    }

    private static string Token(string block, string name) =>
        Regex.Match(block, "^ +" + Regex.Escape(name) + ": (.+);$", RegexOptions.Multiline).Groups[1].Value;

    /// <summary>Base and highlight pairs the derivation must handle: the built-in one and extremes an admin might pick.</summary>
    public static TheoryData<string, string> Pairs => new()
    {
        { ThemeSettings.DefaultBase, ThemeSettings.DefaultHighlight },
        { "#3730a3", "#f4a62a" }, // the former Indigo / Amber
        { "#5a2a5f", "#d9a62e" }, // the former Plum / Gold
        { "#2a6f2b", "#e0457b" },
        { "#ffd84d", "#3355ff" }, // pale base
        { "#ffffff", "#000000" },
        { "#000000", "#ffffff" },
        { "#ff0000", "#00ff00" },
        { "#7f7f7f", "#808080" },
    };

    [Fact]
    public void Built_in_theme_generates_every_token_for_light_and_dark()
    {
        var css = ThemeCss.Generate(ThemeSettings.Default, null);
        var (light, dark, system) = Blocks(css);
        foreach (var name in ModeColors.TokenNames)
        {
            Assert.Matches("^(#[0-9a-f]{6}|#fff|#000|\\d{1,3}, \\d{1,3}, \\d{1,3}|rgba\\(\\d{1,3}, \\d{1,3}, \\d{1,3}, [0-9.]+\\))$", Token(light, name));
            Assert.NotEqual("", Token(dark, name));
            Assert.Equal(Token(dark, name), Token(system, name)); // system-dark repeats the dark block
        }
        Assert.NotEqual(Token(light, "--av-surface"), Token(dark, "--av-surface"));
        Assert.Contains("--av-radius: 6px;", light); // fixed since §24.4
        Assert.Equal(ThemeSettings.DefaultBase, Token(light, "--av-primary"));
        Assert.Equal(ThemeSettings.DefaultHighlight, Token(light, "--av-header-band"));
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_pair_meets_WCAG_AA_in_both_modes(string b, string h)
    {
        var report = new List<string>();
        foreach (var dark in new[] { false, true })
        {
            var m = PaletteDerivation.Derive(b, h, dark);
            var checks = new (string Name, Rgb Fg, Rgb Bg, double Min)[]
            {
                ("text/surface", m.Text, m.Surface, 4.5),
                ("text/canvas", m.Text, m.Canvas, 4.5),
                ("text/elevated", m.Text, m.SurfaceElevated, 4.5),
                ("muted/surface", m.TextMuted, m.Surface, 4.5),
                ("muted/canvas", m.TextMuted, m.Canvas, 4.5),
                ("on-base/base", m.OnPrimary, m.Primary, 4.5),
                ("on-base/base-hover", m.OnPrimary, m.PrimaryHover, 4.5),
                ("link/surface", m.Link, m.Surface, 4.5),
                ("link/canvas", m.Link, m.Canvas, 4.5),
                ("brand-text/primary-subtle", m.PrimaryText, m.PrimarySubtle, 4.5),
                ("on-accent/highlight", m.OnAccent, m.Accent, 4.5),
                ("accent-text/accent-subtle", m.AccentText, m.AccentSubtle, 4.5),
                ("success/tint", m.SuccessFg, m.SuccessBg, 4.5),
                ("warning/tint", m.WarningFg, m.WarningBg, 4.5),
                ("danger/tint", m.DangerFg, m.DangerBg, 4.5),
                ("danger/surface", m.DangerFg, m.Surface, 4.5),
                ("focus/surface", m.FocusOutline, m.Surface, 3.0),
            };
            foreach (var (name, fg, bg, min) in checks)
            {
                var ratio = Rgb.Contrast(fg, bg);
                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{b} {h} {(dark ? "dark" : "light"),-5} {name,-26} {fg.Hex} on {bg.Hex}  {ratio,5:0.00}"));
                Assert.True(ratio >= min, $"{b}/{h} {(dark ? "dark" : "light")} {name}: {ratio:0.00} < {min}");
            }
            if (dark)
            {
                Assert.NotEqual(Rgb.White, m.Text); // never pure white text
                Assert.NotEqual(Rgb.White, m.TextStrong);
            }
        }
        output.WriteLine(string.Join('\n', report));
    }

    [Fact]
    public void Built_in_palette_keeps_its_colours_and_lifts_the_dark_base()
    {
        var light = PaletteDerivation.Derive(ThemeSettings.DefaultBase, ThemeSettings.DefaultHighlight, dark: false);
        var dark = PaletteDerivation.Derive(ThemeSettings.DefaultBase, ThemeSettings.DefaultHighlight, dark: true);
        Assert.Equal(ThemeSettings.DefaultBase, light.Primary.Hex);
        Assert.Equal(ThemeSettings.DefaultHighlight, light.Accent.Hex);
        Assert.Equal(Rgb.White, light.OnPrimary);
        Assert.Equal(Rgb.White, dark.OnPrimary);
        Assert.True(Oklch.From(dark.Primary).L > Oklch.From(Rgb.Parse(ThemeSettings.DefaultBase)).L, "dark base is lifted");

        // A light base keeps its colour on buttons (black text) while links are darkened until they read.
        var pale = PaletteDerivation.Derive("#ffd84d", "#3355ff", dark: false);
        Assert.Equal("#ffd84d", pale.Primary.Hex);
        Assert.Equal(Rgb.Black, pale.OnPrimary);
        Assert.True(Rgb.Contrast(pale.Link, pale.Surface) >= 4.5);
    }

    [Fact]
    public void Each_mode_is_derived_from_its_own_overrides()
    {
        var t = Read((SettingKeys.ThemeLightBase, "#2A6F2B"), (SettingKeys.ThemeDarkHighlight, "#e0457b"));
        Assert.Equal(("#2a6f2b", ThemeSettings.DefaultHighlight), t.LightColours);
        Assert.Equal((ThemeSettings.DefaultBase, "#e0457b"), t.DarkColours);
        var css = ThemeCss.Generate(t, null);
        Assert.Equal(css, ThemeCss.Generate(t, null)); // deterministic
        var (light, dark, system) = Blocks(css);
        var expectedLight = PaletteDerivation.Derive("#2a6f2b", ThemeSettings.DefaultHighlight, dark: false);
        var expectedDark = PaletteDerivation.Derive(ThemeSettings.DefaultBase, "#e0457b", dark: true);
        foreach (var (name, value) in expectedLight.Tokens()) Assert.Equal(value, Token(light, name));
        foreach (var (name, value) in expectedDark.Tokens())
        {
            Assert.Equal(value, Token(dark, name));
            Assert.Equal(value, Token(system, name));
        }
        Assert.Equal("#2a6f2b", Token(light, "--av-primary"));
        Assert.Equal(ThemeSettings.DefaultHighlight, Token(light, "--av-header-band"));
        Assert.Equal(expectedDark.Primary.Hex, Token(dark, "--av-primary"));

        // The favicon follows the light colours.
        Assert.Contains("fill=\"#2a6f2b\"", ThemeCss.Favicon(t));
        Assert.Contains("fill=\"#000000\"", ThemeCss.Favicon(Read((SettingKeys.ThemeLightBase, "#ffd84d"))));
    }

    [Fact]
    public void Stored_themes_from_before_the_mode_overrides_are_upgraded()
    {
        Assert.Equal(ThemeSettings.Default, ThemeSettings.From(_ => null));

        // A designed palette other than Teal / Coral is dropped, stored colours with it.
        Assert.Equal(ThemeSettings.Default, Read((SettingKeys.LegacyThemePalette, "indigo-amber"), (SettingKeys.LegacyThemePrimary, "#123456")));
        Assert.Equal(ThemeSettings.Default, Read((SettingKeys.LegacyThemePalette, "plum-gold")));
        Assert.Equal(ThemeSettings.Default, Read((SettingKeys.LegacyThemePalette, "neon"), (SettingKeys.LegacyThemePrimary, "#123456")));

        // Custom colours become the override of both modes.
        var custom = Read((SettingKeys.LegacyThemePalette, "custom"), (SettingKeys.LegacyThemePrimary, "#2A6F2B"), (SettingKeys.LegacyThemeAccent, "#e0457b"));
        Assert.Equal(("#2a6f2b", "#e0457b", "#2a6f2b", "#e0457b"), (custom.LightBase, custom.LightHighlight, custom.DarkBase, custom.DarkHighlight));

        // Colours stored before palettes existed (no palette row) read as Custom; a missing one is the built-in colour.
        var legacy = Read((SettingKeys.LegacyThemePrimary, "#123456"));
        Assert.Equal(("#123456", ThemeSettings.DefaultHighlight), legacy.LightColours);
        Assert.Equal(("#123456", ThemeSettings.DefaultHighlight), legacy.DarkColours);

        // The radius is ignored; a stored override wins over the old colours; invalid values read as the default.
        Assert.Equal(ThemeSettings.Default, Read((SettingKeys.LegacyThemeRadius, "0")));
        var mixed = Read((SettingKeys.LegacyThemePrimary, "#123456"), (SettingKeys.ThemeDarkBase, "#654321"), (SettingKeys.ThemeLightBase, "red"));
        Assert.Equal(("#123456", "#654321"), (mixed.LightBase, mixed.DarkBase));
        Assert.Equal(ThemeSettings.Default, Read((SettingKeys.ThemeLightHighlight, "#12345g")));
    }

    [Fact]
    public async Task Saving_validates_the_overrides_and_removes_the_old_keys()
    {
        await using var factory = new AppFactory();
        var admin = await AdminAsync(factory);
        var client = factory.CreateClient();

        foreach (var (key, label) in ThemeSettings.ColourKeys.Select(c => (c.Key, c.Label)))
        {
            var bad = await Save(factory, admin, new() { [key] = "teal" });
            Assert.False(bad.Ok);
            Assert.Equal($"{label}: use #rrggbb.", bad.Message);
        }
        Assert.Equal("Palette: set the colour overrides instead.", (await Save(factory, admin, new() { [SettingKeys.LegacyThemePalette] = "plum-gold" })).Message);
        Assert.Equal("Palette: set the colour overrides instead.", (await Save(factory, admin, new() { [SettingKeys.LegacyThemePrimary] = "#123456" })).Message);
        Assert.Equal("Corner radius: fixed at 6 px, leave it out.", (await Save(factory, admin, new() { [SettingKeys.LegacyThemeRadius] = "4" })).Message);
        Assert.Equal(ThemeSettings.Default, factory.Settings.Current.Theme);

        // A theme stored before §24.4 (written straight to the settings, as an old version did) reads as overrides ...
        await factory.Settings.SetAsync(new Dictionary<string, string?>
        {
            [SettingKeys.LegacyThemePalette] = "custom", [SettingKeys.LegacyThemePrimary] = "#2a6f2b",
            [SettingKeys.LegacyThemeAccent] = "#e0457b", [SettingKeys.LegacyThemeRadius] = "12",
        }, null);
        var upgraded = factory.Settings.Current.Theme;
        Assert.Equal(("#2a6f2b", "#e0457b"), upgraded.DarkColours);
        var (light, dark, _) = Blocks(await client.GetStringAsync("/branding/theme.css"));
        Assert.Equal("#2a6f2b", Token(light, "--av-primary"));
        Assert.Contains("--av-radius: 6px;", light);
        Assert.NotEqual("", Token(dark, "--av-primary"));

        // ... and the next save of the Theme section (what the admin page sends) stores them as such and drops the old keys.
        Assert.True((await Save(factory, admin, new()
        {
            [SettingKeys.ThemeLightBase] = "#2a6f2b", [SettingKeys.ThemeLightHighlight] = "#e0457b",
            [SettingKeys.ThemeDarkBase] = "#2a6f2b", [SettingKeys.ThemeDarkHighlight] = "#ffd84d",
            [SettingKeys.LegacyThemePalette] = null, [SettingKeys.LegacyThemePrimary] = null,
            [SettingKeys.LegacyThemeAccent] = null, [SettingKeys.LegacyThemeRadius] = null,
        })).Ok);
        var raw = factory.Settings.Current.Raw;
        Assert.DoesNotContain(SettingKeys.LegacyThemePalette, raw.Keys);
        Assert.DoesNotContain(SettingKeys.LegacyThemePrimary, raw.Keys);
        Assert.DoesNotContain(SettingKeys.LegacyThemeRadius, raw.Keys);
        Assert.Equal(("#2a6f2b", "#ffd84d"), factory.Settings.Current.Theme.DarkColours);
        var (_, darkAfter, _) = Blocks(await client.GetStringAsync("/branding/theme.css?admin=1"));
        Assert.Equal(PaletteDerivation.Derive("#2a6f2b", "#ffd84d", dark: true).Accent.Hex, Token(darkAfter, "--av-accent"));
    }

    [Fact]
    public async Task Visitor_mode_cookie_sets_data_bs_theme_on_pages_and_static_pages()
    {
        await using var factory = new AppFactory();
        var admin = await AdminAsync(factory);
        var client = factory.CreateClient(new() { HandleCookies = false });

        async Task<string> Html(string path, string? mode, HttpMethod? method = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            if (mode is not null) request.Headers.Add("Cookie", $"{ThemeService.ModeCookie}={mode}");
            if (request.Method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Email"] = "x@example.com" });
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return Regex.Match(body, "<html[^>]*>").Value;
        }

        // No cookie, admin default System: no attribute (follows the OS).
        var none = await Html("/", null);
        Assert.DoesNotContain("data-bs-theme", none);
        Assert.DoesNotContain("data-av-theme", none);
        Assert.Contains("data-av-default=\"system\"", none);
        Assert.Contains("data-bs-theme=\"dark\"", await Html("/", "dark"));
        Assert.Contains("data-av-theme=\"dark\"", await Html("/", "dark"));
        Assert.Contains("data-bs-theme=\"light\"", await Html("/Account/Register", "light"));
        Assert.DoesNotContain("data-bs-theme", await Html("/", "purple")); // anything else is no choice
        Assert.Contains("data-bs-theme=\"light\"", await Html("/Account/Login", "light")); // admin-variant pages too

        // Admin default Dark: no cookie is dark, the visitor's light wins.
        Assert.True((await Save(factory, admin, new() { [SettingKeys.ThemePreset] = "dark" })).Ok);
        Assert.Contains("data-bs-theme=\"dark\"", await Html("/", null));
        Assert.Contains("data-av-default=\"dark\"", await Html("/", null));
        var light = await Html("/", "light");
        Assert.Contains("data-bs-theme=\"light\"", light);
        Assert.Contains("data-av-theme=\"light\"", light);

        // The static pages (a form posted without its token) follow the same rule.
        Assert.Contains("data-bs-theme=\"light\"", await Html("/Account/ForgotPassword", "light", HttpMethod.Post));
        Assert.Contains("data-bs-theme=\"dark\"", await Html("/Account/ForgotPassword", null, HttpMethod.Post));
    }

    [Fact]
    public async Task Every_layout_header_has_the_theme_switch()
    {
        await using var factory = new AppFactory();
        var admin = await AdminAsync(factory);
        var browser = factory.CreateBrowser();
        var anonymous = await browser.GetStringAsync("/Account/Login");
        Assert.Contains("data-theme-toggle", anonymous); // PublicLayout
        Assert.Contains("data-theme-choice=\"system\"", anonymous);
        Assert.Contains("aria-pressed=\"false\"", anonymous);
        Assert.Contains("js/theme-switch", anonymous);
        foreach (var layout in new[] { "topbar", "compact", "sidebar" })
        {
            Assert.True((await Save(factory, admin, new() { [SettingKeys.ThemeLayout] = layout == "topbar" ? null : layout })).Ok);
            var signedIn = await LoggedInBrowserAsync(factory, "admin@example.com");
            var page = await signedIn.GetStringAsync("/polls");
            Assert.Contains($"shell-{layout}", page);
            Assert.Contains("data-theme-choice=\"dark\"", page);
        }
    }

    [Fact]
    public async Task Favicon_is_generated_from_the_light_colours_and_linked_by_pages_and_static_pages()
    {
        await using var factory = new AppFactory();
        var admin = await AdminAsync(factory);
        var client = factory.CreateClient();
        var theme = factory.Services.GetRequiredService<ThemeService>();

        var svgLink = (string url) => $"href=\"{url}\""; // Razor encodes the "+" of the type attribute; the href is what matters
        Assert.Contains(svgLink(theme.FaviconUrl), await client.GetStringAsync("/"));
        var icon = await client.GetAsync(theme.FaviconUrl);
        Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType!.MediaType);
        Assert.Equal(BrandingEndpoints.Immutable, icon.Headers.CacheControl!.ToString());
        var svg = await icon.Content.ReadAsStringAsync();
        Assert.Contains($"fill=\"{ThemeSettings.DefaultBase}\"", svg);
        Assert.Contains($"fill=\"{ThemeSettings.DefaultHighlight}\"", svg);
        Assert.Contains("fill=\"#ffffff\"", svg);

        // The static pages (here: a form posted without its token) carry the same icon links.
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreatePollAsync(factory, owner, Ranked("T", "A", "B"));
        var expired = await factory.CreateBrowser().PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Email"] = "x@example.com" }));
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Contains(svgLink(theme.FaviconUrl), await expired.Content.ReadAsStringAsync());

        // Another light base: new URL, its colour.
        var before = theme.FaviconUrl;
        Assert.True((await Save(factory, admin, new() { [SettingKeys.ThemeLightBase] = "#5a2a5f" })).Ok);
        Assert.NotEqual(before, theme.FaviconUrl);
        Assert.Contains("fill=\"#5a2a5f\"", await client.GetStringAsync(theme.FaviconUrl));
        Assert.Equal("no-cache", (await client.GetAsync(before)).Headers.CacheControl!.ToString());
    }

    private static async Task<ClaimsPrincipal> AdminAsync(AppFactory factory)
    {
        var id = await CreateUserAsync(factory, "admin@example.com");
        return await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
    }

    private static Task<AdminOutcome> Save(AppFactory factory, ClaimsPrincipal admin, Dictionary<string, string?> values) =>
        WithScope(factory, sp => sp.GetRequiredService<AdminService>().SaveSettingsAsync(admin, values, "settings.theme"));
}
