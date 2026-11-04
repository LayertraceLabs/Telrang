using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Phase 6 (DESIGN.md §17): branding images, the generated theme stylesheet, the welcome text and the
/// persistent CIDR blocks.
/// </summary>
public partial class Phase6Tests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0];

    private static byte[] Png(int size = 64) => Fill(PngHeader, size);
    private static byte[] Jpeg(int size = 64) => Fill(JpegHeader, size);
    private static byte[] WebP(int size = 64) => Fill([.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBP"u8], size);
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

    private static byte[] Fill(byte[] header, int size)
    {
        var bytes = new byte[Math.Max(size, header.Length)];
        header.CopyTo(bytes, 0);
        for (var i = header.Length; i < bytes.Length; i++) bytes[i] = (byte)(i * 7);
        return bytes;
    }

    private static async Task<(string Id, ClaimsPrincipal Principal)> AdminAsync(AppFactory factory, string email = "admin@example.com")
    {
        var id = await CreateUserAsync(factory, email);
        var principal = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
        return (id, principal);
    }

    private static Task<T> Admin<T>(AppFactory factory, Func<AdminService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<AdminService>()));

    private static Task<AdminOutcome> Upload(AppFactory factory, ClaimsPrincipal admin, string name, byte[] bytes) =>
        Admin(factory, a => a.SaveBrandingAsync(admin, name, new MemoryStream(bytes)));

    private static Task<AdminOutcome> Save(AppFactory factory, ClaimsPrincipal admin, Dictionary<string, string?> values) =>
        Admin(factory, a => a.SaveSettingsAsync(admin, values, "settings.theme"));

    private static HttpClient Peer(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Remove(PeerHeader);
        client.DefaultRequestHeaders.Add(PeerHeader, ip);
        return client;
    }

    // ---- branding files -----------------------------------------------------------------------------

    [Fact]
    public async Task Branding_upload_is_decided_by_file_header_and_size()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var branding = factory.Services.GetRequiredService<BrandingService>();

        Assert.True((await Upload(factory, admin, "logo", Png())).Ok);
        Assert.Equal("image/png", branding.Get("logo")!.ContentType);
        Assert.True((await Upload(factory, admin, "logo", Jpeg())).Ok);
        Assert.Equal("image/jpeg", branding.Get("logo")!.ContentType);
        Assert.False(File.Exists(Path.Combine(branding.Folder, "logo.png"))); // the other extension is removed
        Assert.True((await Upload(factory, admin, "banner", WebP())).Ok);
        Assert.Equal("image/webp", branding.Get("banner")!.ContentType);
        Assert.Empty(Directory.GetFiles(branding.Folder, "*.tmp")); // atomic write leaves nothing behind

        var svg = await Upload(factory, admin, "banner", Svg);
        Assert.False(svg.Ok);
        Assert.Equal("Banner: choose a PNG, JPEG or WebP file.", svg.Message);
        Assert.False((await Upload(factory, admin, "banner", [.. "GIF89a"u8, 1, 2, 3, 4, 5, 6])).Ok);
        Assert.Equal("image/webp", branding.Get("banner")!.ContentType); // a refused upload changes nothing

        var big = await Upload(factory, admin, "logo", Png(1024 * 1024 + 1));
        Assert.False(big.Ok);
        Assert.Equal("Logo: choose a file of 1 MB or less.", big.Message);
        Assert.True((await Upload(factory, admin, "background", Png(1536 * 1024))).Ok); // background: 2 MB
        Assert.False((await Upload(factory, admin, "background", Png(2 * 1024 * 1024 + 1))).Ok);
        Assert.False((await Upload(factory, admin, "icon", Png())).Ok);

        Assert.True((await Admin(factory, a => a.DeleteBrandingAsync(admin, "logo"))).Ok);
        Assert.Null(branding.Get("logo"));
        Assert.False((await Admin(factory, a => a.DeleteBrandingAsync(admin, "logo"))).Ok);

        // Files dropped in by hand: served only when the header matches a known image type and its extension.
        Directory.CreateDirectory(branding.Folder);
        await File.WriteAllBytesAsync(Path.Combine(branding.Folder, "logo.svg"), Png()); // a PNG renamed .svg
        Assert.Null(branding.Get("logo"));
        await File.WriteAllBytesAsync(Path.Combine(branding.Folder, "logo.png"), Svg); // an SVG renamed .png
        Assert.Null(branding.Get("logo"));
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/branding/logo")).StatusCode);
        await File.WriteAllBytesAsync(Path.Combine(branding.Folder, "logo.png"), Png());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/branding/logo")).StatusCode);
    }

    [Fact]
    public async Task Branding_endpoint_serves_anonymously_with_fixed_type_and_versioned_cache()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var bytes = Png(500);
        Assert.True((await Upload(factory, admin, "logo", bytes)).Ok);
        var file = factory.Services.GetRequiredService<BrandingService>().Get("logo")!;
        var client = factory.CreateClient();

        var plain = await client.GetAsync("/branding/logo");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("image/png", plain.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", plain.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-cache", plain.Headers.CacheControl!.ToString());
        Assert.Equal(bytes, await plain.Content.ReadAsByteArrayAsync());

        var versioned = await client.GetAsync($"/branding/logo?v={file.Version}");
        Assert.Equal("public, max-age=31536000, immutable", string.Join(", ", versioned.Headers.GetValues("Cache-Control")));
        Assert.Equal("no-cache", (await client.GetAsync("/branding/logo?v=old")).Headers.CacheControl!.ToString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/branding/banner")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/branding/app.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/branding/..%2Fapp.db")).StatusCode);

        // The layout uses the versioned URL: logo in the navbar (the name is its alt text).
        var home = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains($"src=\"/branding/logo?v={file.Version}\"", home);
        Assert.Contains("class=\"brand-logo\"", home);

        // Banner above the home content.
        Assert.True((await Upload(factory, admin, "banner", Jpeg())).Ok);
        home = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Matches("<img class=\"home-banner\" src=\"/branding/banner\\?v=[0-9]+-[0-9]+\"", home);
    }

    // ---- theme -----------------------------------------------------------------------------------------

    [GeneratedRegex("href=\"/branding/theme.css\\?v=([0-9a-f]+)\"")]
    private static partial Regex ThemeLinkRegex();

    [Fact]
    public async Task Theme_css_reflects_validated_settings_and_appends_custom_css()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var client = factory.CreateClient();

        var home = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        var version = ThemeLinkRegex().Match(home).Groups[1].Value;
        Assert.NotEmpty(version);
        Assert.DoesNotContain("data-bs-theme", home[..home.IndexOf("<head", StringComparison.Ordinal)]); // system preset: no attribute

        var css = await client.GetAsync($"/branding/theme.css?v={version}");
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", string.Join(", ", css.Headers.GetValues("Cache-Control")));
        Assert.Equal("nosniff", css.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains($"--av-primary: {ThemeSettings.DefaultBase};", await css.Content.ReadAsStringAsync());

        // Invalid values are refused at save and change nothing.
        foreach (var (key, value) in new[]
                 {
                     (SettingKeys.ThemeLightBase, "red"), (SettingKeys.ThemeDarkBase, "#ff00"), (SettingKeys.ThemeLightHighlight, "#12345g"),
                     (SettingKeys.ThemeDarkHighlight, "#ffffff; } body { display: none"), (SettingKeys.LegacyThemeRadius, "6"),
                     (SettingKeys.ThemeFontSize, "13"), (SettingKeys.ThemeFontStack, "Comic Sans"), (SettingKeys.ThemePreset, "neon"),
                     (SettingKeys.ThemeBackgroundOverlay, "95"), (SettingKeys.ThemeCustomCss, new string('a', 20 * 1024 + 1)),
                 })
        {
            Assert.False((await Save(factory, admin, new() { [key] = value })).Ok, $"{key}={value}");
        }
        Assert.Equal(ThemeSettings.Default, factory.Settings.Current.Theme);
        Assert.Equal("Light base: use #rrggbb.", (await Save(factory, admin, new() { [SettingKeys.ThemeLightBase] = "red" })).Message);

        Assert.True((await Save(factory, admin, new()
        {
            [SettingKeys.ThemePreset] = "dark", [SettingKeys.ThemeLightBase] = "#FF0000", [SettingKeys.ThemeLightHighlight] = "#00ff00",
            [SettingKeys.ThemeFontSize] = "18", [SettingKeys.ThemeFontStack] = "serif",
            [SettingKeys.ThemeCustomCss] = ".site-footer { letter-spacing: 1px; }",
        })).Ok);

        home = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("<html lang=\"en\" data-bs-theme=\"dark\"", home);
        var newVersion = ThemeLinkRegex().Match(home).Groups[1].Value;
        Assert.NotEqual(version, newVersion);
        Assert.Equal("no-cache", (await client.GetAsync($"/branding/theme.css?v={version}")).Headers.CacheControl!.ToString()); // stale version
        var text = await client.GetStringAsync($"/branding/theme.css?v={newVersion}");
        Assert.Contains("--av-primary: #ff0000;", text);
        Assert.Contains("--av-primary-rgb: 255, 0, 0;", text);
        Assert.Contains("--av-on-primary: #000;", text); // black has more contrast on pure red
        Assert.Contains("--av-accent: #00ff00;", text);
        Assert.Contains("--av-radius: 6px;", text); // fixed (§24.4)
        Assert.Contains("html { font-size: 112.5%; }", text);
        Assert.Contains("--av-font: Charter,", text);
        Assert.EndsWith(".site-footer { letter-spacing: 1px; }\n", text); // custom CSS last, verbatim

        // Background: only when enabled and a file exists; the overlay is the page colour at the set strength.
        Assert.True((await Save(factory, admin, new() { [SettingKeys.ThemeBackgroundEnabled] = "true", [SettingKeys.ThemeBackgroundOverlay] = "40" })).Ok);
        Assert.DoesNotContain("background-image", await client.GetStringAsync("/branding/theme.css"));
        Assert.True((await Upload(factory, admin, "background", WebP())).Ok);
        var bgVersion = factory.Services.GetRequiredService<BrandingService>().Get("background")!.Version;
        text = await client.GetStringAsync("/branding/theme.css");
        Assert.Contains($"url(\"/branding/background?v={bgVersion}\")", text);
        Assert.Contains("rgba(var(--bs-body-bg-rgb), 0.4)", text);
    }

    [Fact]
    public async Task App_css_defaults_match_the_generated_default_theme()
    {
        await using var factory = new AppFactory();
        var client = factory.CreateClient();
        var appCss = await client.GetStringAsync("/app.css");
        var generated = ThemeCss.Generate(ThemeSettings.Default, null);
        foreach (Match m in Regex.Matches(generated, "^  (--av-[a-z-]+): (.+);$", RegexOptions.Multiline))
        {
            Assert.Contains($"    {m.Groups[1].Value}: {m.Groups[2].Value};", appCss);
        }
        // CSP stays strict: no inline styles allowed anywhere.
        var csp = (await client.GetAsync("/")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("style-src 'self';", csp);
    }

    // ---- welcome text -------------------------------------------------------------------------------------

    [Fact]
    public async Task Welcome_markdown_renders_without_raw_html_and_falls_back_to_the_default_line()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var client = factory.CreateClient();
        const string Default = "Ranked choice, points and date polls, shared by link.";
        Assert.Contains(Default, await client.GetStringAsync("/"));

        Assert.True((await Save(factory, admin, new()
        {
            [SettingKeys.HomeWelcomeMarkdown] = "**Club votes** <script>alert(1)</script>\n\n<img src=x onerror=alert(2)> [x](javascript:alert(3))",
        })).Ok);
        var home = await client.GetStringAsync("/");
        Assert.Contains("<strong>Club votes</strong>", home);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", home);
        Assert.DoesNotContain("<script>alert", home);
        Assert.DoesNotContain("<img src=x", home);
        Assert.DoesNotContain("javascript:", home);
        Assert.DoesNotContain(Default, home);

        Assert.False((await Save(factory, admin, new() { [SettingKeys.HomeWelcomeMarkdown] = new string('x', 8 * 1024 + 1) })).Ok);
        Assert.True((await Save(factory, admin, new() { [SettingKeys.HomeWelcomeMarkdown] = "" })).Ok);
        Assert.Contains(Default, await client.GetStringAsync("/"));
    }

    // ---- persistent CIDR blocks ----------------------------------------------------------------------------

    [Fact]
    public async Task Blocked_ranges_block_matching_ipv4_and_ipv6_clients_until_they_expire()
    {
        var clock = new MutableTime(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["ReverseProxy:KnownProxies"] = "127.0.0.1" }, services: s =>
        {
            AddFakePeer(s);
            s.AddSingleton<TimeProvider>(clock);
        });
        var (_, admin) = await AdminAsync(factory);

        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "198.51.100.0/24", "spam wave", 1))).Ok);
        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "2001:db8:1::/48", null, null))).Ok);
        Assert.False((await Admin(factory, a => a.AddRangeAsync(admin, "198.51.100.0/24", null, null))).Ok); // duplicate

        var blocked = await Peer(factory.CreateBrowser(), "198.51.100.200").GetAsync("/");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var page = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("<p>Access from your network is blocked.</p>", page);
        Assert.DoesNotContain("<script", page);
        Assert.Equal(HttpStatusCode.Forbidden, (await Peer(factory.CreateBrowser(), "::ffff:198.51.100.3").GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Peer(factory.CreateBrowser(), "2001:db8:1:abcd::5").GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "2001:db8:2::5").GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "198.51.101.1").GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "198.51.100.200").GetAsync("/app.css")).StatusCode); // static files pass

        // Same client address logic as the tracker: X-Forwarded-For from a trusted proxy (loopback, configured above).
        var viaProxy = Peer(factory.CreateBrowser(), "127.0.0.1");
        viaProxy.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.50");
        Assert.Equal(HttpStatusCode.Forbidden, (await viaProxy.GetAsync("/")).StatusCode);
        var untrusted = Peer(factory.CreateBrowser(), "203.0.113.9");
        untrusted.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.50");
        Assert.Equal(HttpStatusCode.OK, (await untrusted.GetAsync("/")).StatusCode); // header ignored from an untrusted peer

        var rows = (await Admin(factory, a => a.ListRangesAsync(admin)))!;
        Assert.Equal(2, rows.Count);
        var v4 = rows.Single(r => r.Cidr == "198.51.100.0/24");
        Assert.Equal(("spam wave", "admin@example.com", clock.Now.AddDays(1)), (v4.Note, v4.CreatedBy, v4.ExpiresAt));
        Assert.Null(rows.Single(r => r.Cidr == "2001:db8:1::/48").ExpiresAt);

        // Expiry: stops blocking at once; the row is purged on the next load.
        clock.Now = clock.Now.AddDays(1).AddMinutes(1);
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "198.51.100.200").GetAsync("/")).StatusCode);
        Assert.Equal("2001:db8:1::/48", Assert.Single((await Admin(factory, a => a.ListRangesAsync(admin)))!).Cidr);
        Assert.Equal(1, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().BlockedRanges.CountAsync()));

        // Remove.
        var v6 = (await Admin(factory, a => a.ListRangesAsync(admin)))!.Single();
        Assert.True((await Admin(factory, a => a.RemoveRangeAsync(admin, v6.Id))).Ok);
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "2001:db8:1::5").GetAsync("/")).StatusCode);
        Assert.False((await Admin(factory, a => a.RemoveRangeAsync(admin, v6.Id))).Ok);
        Assert.Contains(await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AdminActionLogs.Select(l => l.Action).ToListAsync()),
            a => a == "abuse.range-add");
    }

    [Fact]
    public async Task Admins_are_never_blocked_and_loopback_or_proxies_cannot_be_blocked()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["ReverseProxy:KnownNetworks"] = "10.0.0.0/8,172.16.0.0/12,fd00::/8" }, AddFakePeer);
        var (_, admin) = await AdminAsync(factory);
        await CreateUserAsync(factory, "user@example.com");

        var adminBrowser = Peer(factory.CreateBrowser(), "192.0.2.10");
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(adminBrowser, "admin@example.com", Password)).StatusCode);
        var userBrowser = Peer(factory.CreateBrowser(), "192.0.2.11");
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(userBrowser, "user@example.com", Password)).StatusCode);

        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "192.0.2.0/24", null, null))).Ok);
        Assert.Equal(HttpStatusCode.OK, (await adminBrowser.GetAsync("/admin?tab=abuse")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userBrowser.GetAsync("/polls")).StatusCode); // signed in, not an admin

        foreach (var cidr in new[] { "127.0.0.1", "127.0.0.0/8", "127.5.0.0/16", "::1", "::/0", "0.0.0.0/0", "10.0.0.0/8", "10.1.2.3", "172.16.0.0/16", "fd00::/8" })
        {
            var outcome = await Admin(factory, a => a.AddRangeAsync(admin, cidr, null, null));
            Assert.False(outcome.Ok, cidr);
        }
        Assert.Equal("That range includes loopback.", (await Admin(factory, a => a.AddRangeAsync(admin, "127.0.0.1", null, null))).Message);
        Assert.Equal("That range includes a trusted reverse proxy.", (await Admin(factory, a => a.AddRangeAsync(admin, "10.1.2.3", null, null))).Message);
        Assert.False((await Admin(factory, a => a.AddRangeAsync(admin, "not a range", null, null))).Ok);
        Assert.False((await Admin(factory, a => a.AddRangeAsync(admin, "203.0.113.0/24", new string('n', 201), null))).Ok);
        Assert.False((await Admin(factory, a => a.AddRangeAsync(admin, "203.0.113.0/24", null, 0))).Ok);

        // Not an admin: refused.
        var userPrincipal = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync((await users.FindByEmailAsync("user@example.com"))!);
        });
        Assert.False((await Admin(factory, a => a.AddRangeAsync(userPrincipal, "203.0.113.0/24", null, null))).Ok);
        Assert.Null(await Admin(factory, a => a.ListRangesAsync(userPrincipal)));
    }

    [Fact]
    public async Task Known_proxies_from_configuration_cannot_be_blocked()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["ReverseProxy:KnownProxies"] = "203.0.113.5" });
        var (_, admin) = await AdminAsync(factory);
        Assert.False((await Admin(factory, a => a.AddRangeAsync(admin, "203.0.113.0/24", null, null))).Ok);
        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "10.0.0.0/16", null, null))).Ok); // private ranges are not trusted now
    }

    // ---- R2-ACC-7: a typo cannot block half the internet, nor the admin; a startup switch clears every range -------

    [Fact]
    public async Task R2_ACC_7_host_bits_short_prefixes_and_the_admins_own_address_are_refused()
    {
        await using var factory = new AppFactory(services: AddFakePeer);
        var (_, admin) = await AdminAsync(factory);

        foreach (var (cidr, message) in new[]
                 {
                     ("198.51.100.7/1", "Use 128.0.0.0/1."),
                     ("198.51.100.7/24", "Use 198.51.100.0/24."),
                     ("2001:db8::1/48", "Use 2001:db8::/48."),
                     ("32.0.0.0/3", "Use a prefix of /16 or longer."),
                     ("128.0.0.0/1", "Use a prefix of /16 or longer."),
                     ("10.0.0.0/15", "Use a prefix of /16 or longer."),
                     ("8000::/1", "Use a prefix of /32 or longer."),
                     ("2001:db8::/31", "Use a prefix of /32 or longer."),
                 })
        {
            var refused = await Admin(factory, a => a.AddRangeAsync(admin, cidr, null, null));
            Assert.Equal((false, message), (refused.Ok, refused.Message));
        }
        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "10.0.0.0/16", null, null))).Ok);
        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "2001:db8::/32", null, null))).Ok);
        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "198.51.100.7", null, null))).Ok);
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "203.0.113.9").GetAsync("/Account/Login")).StatusCode);

        // The acting admin's own address (from the request or the circuit's captured one).
        async Task<AdminOutcome> AddFrom(string address, string cidr) => await WithScope(factory, sp =>
        {
            sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext =
                new Microsoft.AspNetCore.Http.DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse(address) } };
            return sp.GetRequiredService<AdminService>().AddRangeAsync(admin, cidr, null, null);
        });
        var own = await AddFrom("203.0.113.9", "203.0.113.0/24");
        Assert.Equal((false, "That range includes your own address."), (own.Ok, own.Message));
        Assert.True((await AddFrom("::ffff:192.0.2.9", "198.18.0.0/16")).Ok);
        Assert.False((await AddFrom("::ffff:192.0.2.9", "192.0.2.0/24")).Ok);
    }

    [Fact]
    public async Task R2_ACC_7_a_startup_switch_clears_every_blocked_range()
    {
        var dir = Path.Combine(Path.GetTempPath(), "telrang-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var factory = new AppFactory(new Dictionary<string, string?> { ["App:DataDir"] = dir }, AddFakePeer))
            {
                var (_, admin) = await AdminAsync(factory);
                Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "203.0.113.0/24", null, null))).Ok);
                Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "198.51.100.0/24", null, null))).Ok);
                Assert.Equal(HttpStatusCode.Forbidden, (await Peer(factory.CreateBrowser(), "203.0.113.9").GetAsync("/Account/Login")).StatusCode);
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            await using (var restarted = new AppFactory(new Dictionary<string, string?>
            {
                ["App:DataDir"] = dir,
                ["Abuse:ClearBlockedRangesOnStart"] = "true",
            }, AddFakePeer))
            {
                Assert.Equal(HttpStatusCode.OK, (await Peer(restarted.CreateBrowser(), "203.0.113.9").GetAsync("/Account/Login")).StatusCode);
                Assert.Equal(0, await WithScope(restarted, sp => sp.GetRequiredService<ApplicationDbContext>().BlockedRanges.CountAsync()));
                Assert.Contains(restarted.Services.GetRequiredService<Telrang.Web.Features.Status.LogRing>().Snapshot(),
                    e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("removed all 2 blocked ranges"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
