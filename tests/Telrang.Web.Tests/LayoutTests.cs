using System.Net;
using System.Reflection;
using System.Security.Claims;
using Telrang.Web.Components.Account.Shared;
using Telrang.Web.Components.Layout;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>App shells (DESIGN.md §11): Theme.Layout picks the signed-in shell; anonymous-facing pages use PublicLayout.</summary>
public class LayoutTests
{
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

    /// <summary>The footer credit (DESIGN.md §24.5), in every layout.</summary>
    private const string FooterCredit = "<a href=\"https://layertrace.eu\" rel=\"noopener\">Telrang by LayerTrace Labs</a>";

    private static Task<AdminOutcome> SaveLayout(AppFactory factory, ClaimsPrincipal admin, string? value) =>
        WithScope(factory, sp => sp.GetRequiredService<AdminService>()
            .SaveSettingsAsync(admin, new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = value }, "settings.theme"));

    private static async Task<string> Html(HttpClient client, string path)
    {
        var response = await GetPageAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData(null, "shell-topbar", "class=\"user-menu\"")]
    [InlineData("topbar", "shell-topbar", "class=\"user-menu\"")]
    [InlineData("sidebar", "shell-sidebar", "class=\"user-block\"")]
    [InlineData("compact", "shell-compact", "class=\"user-menu\"")]
    public async Task Each_layout_renders_the_nav_and_the_user_menu_for_a_signed_in_user(string? layout, string shell, string menu)
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "alice@example.com");
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = layout }, null);
        var browser = await LoggedInBrowserAsync(factory, "alice@example.com");

        foreach (var path in new[] { "/polls", "/my/votes", "/Account/Manage", "/" }) // "/": static, still the shell (§24.1)
        {
            var html = await Html(browser, path);
            Assert.Contains($"class=\"shell {shell}\"", html);
            Assert.Contains("class=\"app-nav ", html);
            Assert.Contains("href=\"polls\"", html);
            Assert.Contains("href=\"my/votes\"", html);
            Assert.DoesNotContain("href=\"admin\"", html); // not an admin
            Assert.Contains(menu, html);
            Assert.Contains(">alice<", html); // the part before @, never the email in the header (§24.2)
            Assert.DoesNotContain("alice@example.com", html[..html.IndexOf("<main", StringComparison.Ordinal)]);
            Assert.Contains("href=\"Account/Manage\"", html);
            Assert.Contains("action=\"Account/Logout\"", html);
            Assert.Contains("class=\"site-footer", html);
            Assert.Contains(FooterCredit, html); // §24.5
            Assert.DoesNotContain(" style=\"", html); // CSP: no inline styles
        }

        // Admins get the Admin link in every shell.
        var (_, _) = await AdminAsync(factory, "root@example.com");
        var admin = await LoggedInBrowserAsync(factory, "root@example.com");
        Assert.Contains("href=\"admin\"", await Html(admin, "/polls"));
    }

    [Fact]
    public async Task Public_layout_serves_anonymous_poll_and_login_pages_without_app_nav()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(results: ResultsVisibility.Open));
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = "sidebar" }, null);

        // Anonymous: slim header with "Log in" back to the page, no app nav, whatever the signed-in layout is.
        var anonymous = factory.CreateBrowser();
        foreach (var path in new[] { "/", $"/p/{id}", $"/p/{id}/results", "/Account/Login", "/Account/Register", "/Account/ForgotPassword", "/about-missing" })
        {
            var response = await GetPageAsync(anonymous, path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("class=\"shell shell-public\"", html);
            Assert.DoesNotContain("class=\"app-nav", html);
            Assert.DoesNotContain("shell-sidebar", html);
            Assert.Contains(FooterCredit, html); // §24.5
        }
        var vote = await Html(anonymous, $"/p/{id}");
        Assert.Contains($"href=\"Account/Login?ReturnUrl=%2Fp%2F{id}\"", vote);
        Assert.DoesNotContain("Account/Login?ReturnUrl", await Html(anonymous, "/Account/Login")); // no "Log in" link on the login page

        // Signed in, on a public page: the account menu (with My polls / My votes) instead of "Log in", still no app nav.
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");
        var signedIn = await (await GetPageAsync(browser, "/about-missing")).Content.ReadAsStringAsync(); // the not-found page
        Assert.Contains("class=\"shell shell-public\"", signedIn);
        Assert.DoesNotContain("class=\"app-nav", signedIn);
        Assert.Contains("class=\"user-menu\"", signedIn);
        Assert.Contains("href=\"my/votes\"", signedIn);
        Assert.Contains("action=\"Account/Logout\"", signedIn);
        Assert.DoesNotContain("Account/Login?ReturnUrl", signedIn);

        // The poll pages (DESIGN.md §33) give a signed-in visitor the configured shell with its navigation, still static.
        foreach (var path in new[] { $"/p/{id}", $"/p/{id}/results" })
        {
            var html = await Html(browser, path);
            Assert.Contains("shell-sidebar", html);
            Assert.Contains("class=\"app-nav", html);
            Assert.DoesNotContain("<!--Blazor:", html);
        }
    }

    [Fact]
    public async Task Admin_settings_accept_the_layout_values_and_refuse_an_invalid_one()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        Assert.Equal(ThemeLayout.TopBar, factory.Settings.Current.Theme.Layout);

        foreach (var (value, expected) in new[] { ("sidebar", ThemeLayout.Sidebar), ("Compact", ThemeLayout.Compact), ("topbar", ThemeLayout.TopBar) })
        {
            Assert.True((await SaveLayout(factory, admin, value)).Ok, value);
            Assert.Equal(expected, factory.Settings.Current.Theme.Layout);
        }
        Assert.True((await SaveLayout(factory, admin, "sidebar")).Ok);

        var refused = await SaveLayout(factory, admin, "grid");
        Assert.False(refused.Ok);
        Assert.Equal("Layout: choose one from the list.", refused.Message);
        Assert.Equal(ThemeLayout.Sidebar, factory.Settings.Current.Theme.Layout); // unchanged

        Assert.True((await SaveLayout(factory, admin, null)).Ok); // blank = back to the default
        Assert.Equal(ThemeLayout.TopBar, factory.Settings.Current.Theme.Layout);

        // A value edited into the database by hand falls back to the default.
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = "<script>" }, null);
        Assert.Equal(ThemeLayout.TopBar, factory.Settings.Current.Theme.Layout);

        // The Theme section offers the three layouts.
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var page = await Html(browser, "/admin?tab=settings");
        Assert.Contains("id=\"theme-layout\"", page);
        foreach (var (_, value, label) in ThemeSettings.Layouts)
        {
            Assert.Matches($"<option value=\"{value}\"[^>]*>{label}</option>", page);
        }
    }

    [Fact]
    public void Every_page_declares_its_layout()
    {
        string[] publicPrefixes = ["/p/", "/setup", "/notify/", "/about", "/Error", "/not-found", "/Account/"];
        var pages = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && t.GetCustomAttributes<RouteAttribute>().Any())
            .ToList();
        Assert.NotEmpty(pages);
        foreach (var page in pages)
        {
            var route = page.GetCustomAttributes<RouteAttribute>().First().Template;
            var layout = page.GetCustomAttribute<LayoutAttribute>()?.LayoutType;
            Assert.True(layout is not null, $"{page.Name} ({route}) has no @layout");
            // The home page (DESIGN.md §24.1) and the poll pages (§33) use MainLayout, which picks PublicLayout for a visitor
            // who is not signed in.
            if (route is "/" or "/p/{Id}" or "/p/{Id}/results") { Assert.True(layout == typeof(MainLayout), $"{route}: {layout!.Name}"); continue; }
            var isPublic = (publicPrefixes.Any(p => route.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                               && !route.StartsWith("/Account/Manage", StringComparison.OrdinalIgnoreCase));
            if (isPublic) Assert.True(layout == typeof(PublicLayout), $"{route}: {layout!.Name}");
            else Assert.True(layout == typeof(MainLayout) || layout == typeof(ManageLayout), $"{route}: {layout!.Name}");
        }
    }
}
