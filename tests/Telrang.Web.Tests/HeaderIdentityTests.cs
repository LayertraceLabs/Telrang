using System.Net;
using Telrang.Web.Components.Layout;
using Telrang.Web.Data;
using Telrang.Web.Features.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>The header never shows the email (DESIGN.md §24.2): display name, else the part before @, or hidden.</summary>
public class HeaderIdentityTests
{
    private const string Email = "alice.smith@example.com";

    private static async Task<string> Html(HttpClient client, string path)
    {
        var response = await GetPageAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Everything before the page content: the header, nav and account menu of every shell.</summary>
    private static string Header(string html)
    {
        var main = html.IndexOf("<main", StringComparison.Ordinal);
        Assert.True(main > 0, "no <main>");
        return html[..main];
    }

    /// <summary>As Blazor writes text (non-ASCII as character references).</summary>
    private static string Enc(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    private static Task SetProfileAsync(AppFactory factory, string userId, string? displayName, bool hide) =>
        WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            user.DisplayName = displayName;
            user.HideIdentity = hide;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
            return true;
        });

    private static Task<HttpResponseMessage> SaveProfileAsync(HttpClient browser, string displayName, bool hide)
    {
        var fields = new Dictionary<string, string> { ["Input.DisplayName"] = displayName, ["Input.TimeZone"] = "" };
        if (hide) fields["Input.HideIdentity"] = "true";
        return AppFactory.SubmitFormAsync(browser, "/Account/Manage", "profile", fields);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sidebar")]
    [InlineData("compact")]
    public async Task No_page_shows_the_email_in_the_header_and_the_local_part_stands_in(string? layout)
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, Email);
        var id = await CreatePollAsync(factory, userId, RankedDraft(results: ResultsVisibility.Open));
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = layout }, null);
        var browser = await LoggedInBrowserAsync(factory, Email);

        foreach (var path in new[] { "/", "/polls", "/my/votes", $"/polls/{id}/share", $"/p/{id}", $"/p/{id}/results", "/about-missing" })
        {
            var response = await GetPageAsync(browser, path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Email, html); // not in the header, not in a title attribute, nowhere
            Assert.Contains(">alice.smith<", Header(html));
            Assert.Contains(">A</span>", Header(html)); // the avatar initial
        }

        // Account pages show the email in their content only.
        foreach (var path in new[] { "/Account/Manage", "/Account/Manage/Email" })
        {
            var html = await Html(browser, path);
            Assert.DoesNotContain(Email, Header(html));
            Assert.Contains(">alice.smith<", Header(html));
            Assert.Contains(Email, html);
        }
    }

    [Fact]
    public async Task The_display_name_wins_and_its_initial_is_the_avatar()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, Email);
        await SetProfileAsync(factory, userId, "  Zoë <b>  ", hide: false);
        var browser = await LoggedInBrowserAsync(factory, Email); // the claim is set at sign-in

        var header = Header(await Html(browser, "/polls"));
        Assert.Contains($">{Enc("Zoë <b>")}<", header);
        Assert.Contains(">Z</span>", header);
        Assert.DoesNotContain("alice.smith", header);
        Assert.DoesNotContain("<b>", header);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sidebar")]
    public async Task Hiding_the_name_leaves_a_generic_avatar_and_saving_the_profile_applies_at_once(string? layout)
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, Email);
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = layout }, null);
        var browser = await LoggedInBrowserAsync(factory, Email);

        var profile = await Html(browser, "/Account/Manage");
        Assert.Contains("id=\"Input.HideIdentity\"", profile);
        Assert.Contains("Hide name in the header", profile);
        Assert.Contains("aria-label=\"About Hide name in the header\"", profile);
        Assert.Contains("data-static-help", profile); // static page: js/menus.js toggles the help

        // Save the profile (display name and hide): the same browser sees it on the next page, without signing in again.
        var saved = await SaveProfileAsync(browser, "Zoë", hide: true);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        foreach (var path in new[] { "/polls", "/", "/Account/Manage" })
        {
            var header = Header(await Html(browser, path));
            Assert.DoesNotContain(Enc("Zoë"), header);
            Assert.DoesNotContain("alice.smith", header);
            Assert.Contains("user-avatar user-avatar-generic", header);
            Assert.Contains("#user\"", header); // the user icon
            Assert.Contains(layout == "sidebar" ? "class=\"user-name\">Account<" : "class=\"menu-header\">Account<", header);
            Assert.DoesNotContain("user-menu-name", header);
        }
        Assert.Matches("<input[^>]*id=\"Input.HideIdentity\"[^>]*checked", await Html(browser, "/Account/Manage"));

        // Turned off again: the display name comes back.
        Assert.Equal(HttpStatusCode.Redirect, (await SaveProfileAsync(browser, "Zoë", hide: false)).StatusCode);
        var back = Header(await Html(browser, "/polls"));
        Assert.Contains($">{Enc("Zoë")}<", back);
        Assert.DoesNotContain("user-avatar-generic", back);
    }

    [Fact]
    public async Task Other_sessions_pick_up_the_change_at_the_next_security_stamp_validation()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var userId = await CreateUserAsync(factory, Email);
        var other = await LoggedInBrowserAsync(factory, Email);
        Assert.Contains(">alice.smith<", Header(await Html(other, "/polls")));

        await SetProfileAsync(factory, userId, "Zoë", hide: false);
        clock.Now += TimeSpan.FromMinutes(2); // past SecurityStampValidatorOptions.ValidationInterval (1 min)
        Assert.Contains($">{Enc("Zoë")}<", Header(await Html(other, "/polls")));
    }

    [Theory]
    [InlineData("alice@example.com", "alice")]
    [InlineData("a.b@c@example.com", "a.b@c")]
    [InlineData("no-at-sign", "no-at-sign")]
    [InlineData("@example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Local_part(string? email, string? expected) => Assert.Equal(expected, HeaderIdentity.LocalPart(email));

    [Theory]
    [InlineData("zoë", "Z")]
    [InlineData("  bob", "B")]
    [InlineData("éva", "É")]
    public void Initial(string text, string expected) => Assert.Equal(expected, HeaderIdentity.Initial(text));
}
