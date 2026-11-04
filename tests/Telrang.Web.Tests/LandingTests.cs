using System.Net;
using Telrang.Web.Data;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Where sign-ins land (DESIGN.md §23.3): My polls, unless a local return URL is given; the brand leads home.</summary>
public class LandingTests
{
    [Theory]
    [InlineData("topbar")]
    [InlineData("sidebar")]
    [InlineData("compact")]
    public async Task The_brand_in_every_signed_in_shell_links_home(string layout)
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "alice@example.com");
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = layout }, null);
        var browser = await LoggedInBrowserAsync(factory, "alice@example.com");

        var html = await (await GetPageAsync(browser, "/polls")).Content.ReadAsStringAsync();
        Assert.Contains($"shell-{layout}", html);
        Assert.Contains("<base href=\"/\">", html);
        Assert.Contains("<a class=\"app-brand\" href>", html); // empty: the base, "/"
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(browser, "/", maxRedirects: 0)).StatusCode); // nothing redirects "/"
    }

    private static string PathOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return new Uri(new Uri("http://localhost/"), response.Headers.Location!).PathAndQuery;
    }

    private static Task<string> EnableTwoFactorAsync(AppFactory factory, string userId) => WithScope(factory, async sp =>
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId))!;
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
        return (await users.GetAuthenticatorKeyAsync(user))!;
    });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string? returnUrl) =>
        AppFactory.SubmitFormAsync(browser, returnUrl is null ? "/Account/Login" : "/Account/Login?ReturnUrl=" + Uri.EscapeDataString(returnUrl),
            "login", new Dictionary<string, string> { ["Input.Email"] = "me@example.com", ["Input.Password"] = Password });

    [Theory]
    [InlineData(null, "/polls")]
    [InlineData("/my/votes", "/my/votes")]
    [InlineData("/", "/")]
    [InlineData("https://evil.example/", "/polls")]
    [InlineData("//evil.example/", "/polls")]
    public async Task Password_sign_in_lands_on_my_polls_unless_a_local_return_url_is_given(string? returnUrl, string expected)
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "me@example.com");
        Assert.Equal(expected, PathOf(await LoginAsync(factory.CreateBrowser(), returnUrl)));
    }

    [Theory]
    [InlineData(null, "/polls")]
    [InlineData("/my/votes", "/my/votes")]
    public async Task Two_factor_sign_in_lands_on_my_polls_unless_a_return_url_is_given(string? returnUrl, string expected)
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "me@example.com");
        var key = await EnableTwoFactorAsync(factory, userId);

        var browser = factory.CreateBrowser();
        var second = PathOf(await LoginAsync(browser, returnUrl));
        Assert.StartsWith("/Account/LoginWith2fa", second);
        var done = await AppFactory.SubmitFormAsync(browser, second, "login-with-2fa",
            new Dictionary<string, string> { ["Input.TwoFactorCode"] = Phase5aTests.Totp(key) });
        Assert.Equal(expected, PathOf(done));
    }

    [Fact]
    public async Task Recovery_code_sign_in_lands_on_my_polls()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "me@example.com");
        await EnableTwoFactorAsync(factory, userId);
        var code = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(userId))!, 1))!.Single();
        });

        var browser = factory.CreateBrowser();
        Assert.StartsWith("/Account/LoginWith2fa", PathOf(await LoginAsync(browser, null)));
        var done = await AppFactory.SubmitFormAsync(browser, "/Account/LoginWithRecoveryCode", "login-with-recovery-code",
            new Dictionary<string, string> { ["Input.RecoveryCode"] = code });
        Assert.Equal("/polls", PathOf(done));
    }

    [Fact]
    public async Task Completing_a_setup_link_continues_to_my_polls()
    {
        await using var factory = new AppFactory();
        var adminId = await CreateUserAsync(factory, "admin@example.com");
        var admin = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(adminId))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
        var outcome = await WithScope(factory, sp => sp.GetRequiredService<Features.Admin.AdminService>().CreateAccountAsync(admin, "https://localhost/"));
        Assert.True(outcome.Ok, outcome.Message);

        var browser = factory.CreateBrowser();
        var done = await AppFactory.SubmitFormAsync(browser, new Uri(outcome.Link!).AbsolutePath, "setup-account", new Dictionary<string, string>
        {
            ["Input.Email"] = "new@example.com",
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
        });
        Assert.Equal(AccountGate.EnrolPath, PathOf(done));

        var enrolled = await Phase5aTests.EnrolTotpAsync(factory, browser, "new@example.com");
        Assert.Contains("href=\"polls\">Continue</a>", await enrolled.Content.ReadAsStringAsync());
    }
}
