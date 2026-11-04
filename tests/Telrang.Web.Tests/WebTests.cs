using System.Net;
using System.Text.RegularExpressions;
using Telrang.Web.Data;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Telrang.Web.Tests;

public class WebTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task Healthz_returns_200()
    {
        await using var factory = new AppFactory();
        var response = await factory.CreateBrowser().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Data_dir_holds_database_and_keys()
    {
        await using var factory = new AppFactory();
        await factory.CreateBrowser().GetAsync("/"); // forces startup + a Data Protection key

        Assert.Equal(!AppFactory.OnPostgres, File.Exists(Path.Combine(factory.DataDir, "app.db"))); // PostgreSQL: keys only
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(factory.DataDir, "keys"), "*.xml"));
    }

    [Fact]
    public async Task Home_has_security_headers_and_nonced_import_map()
    {
        await using var factory = new AppFactory();
        var response = await factory.CreateBrowser().GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));

        var nonce = Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains($"<script type=\"importmap\" nonce=\"{nonce}\">", html);
        Assert.Contains("Telrang", html); // App:Name in brand/title
    }

    [Theory]
    [InlineData("/polls")]
    [InlineData("/polls/new")]
    [InlineData("/admin")]
    public async Task Protected_pages_redirect_anonymous_users_to_login(string path)
    {
        await using var factory = new AppFactory();
        var response = await factory.CreateBrowser().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Register_then_login_works_and_registrants_are_not_admins()
    {
        await using var factory = new AppFactory();

        // Register (no sign-in: a taken address must get the same answer, pentest AV-10; the email is unverified until
        // the mailed link is opened).
        var registrar = factory.CreateBrowser();
        var registered = await AppFactory.RegisterAsync(registrar, "first@example.com", Password);
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        Assert.Contains("Account/RegisterConfirmation", registered.Headers.Location!.ToString());
        Assert.DoesNotContain(registered.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith(".AspNetCore.Identity.Application="));

        // Not before the email is verified (pentest R2-ACC-3): the same answer as for an unknown address.
        var early = await AppFactory.LoginAsync(factory.CreateBrowser(), "first@example.com", Password);
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Contains("Invalid login attempt", await early.Content.ReadAsStringAsync());
        await TestUtil.ConfirmEmailAsync(factory, "first@example.com");

        // Fresh browser: log in with the same credentials.
        var browser = factory.CreateBrowser();
        var login = await AppFactory.LoginAsync(browser, "first@example.com", Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.DoesNotContain("Lockout", login.Headers.Location!.ToString());
        Assert.DoesNotContain("LoginWith2fa", login.Headers.Location!.ToString());

        var polls = await browser.GetAsync("/polls");
        Assert.Equal(HttpStatusCode.OK, polls.StatusCode);
        var pollsHtml = await polls.Content.ReadAsStringAsync();
        Assert.Contains("My polls", pollsHtml);
        Assert.Contains("href=\"polls/new\"", pollsHtml);

        // The first registrant is no longer an admin (the setup key creates the admin, §14.1).
        var denied = await browser.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/Account/AccessDenied", denied.Headers.Location!.ToString());

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var first = (await users.FindByEmailAsync("first@example.com"))!;
        Assert.False(await users.IsInRoleAsync(first, AppRoles.Admin));
        Assert.True(first.SelfRegistered);

        // Identity V3 hash layout: [0]=0x01, [1..4]=PRF, [5..8]=iteration count (big-endian).
        var hash = Convert.FromBase64String(first.PasswordHash!);
        Assert.Equal(0x01, hash[0]);
        Assert.Equal(600_000, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(5, 4)));
    }

    [Fact]
    public async Task Short_password_is_rejected()
    {
        await using var factory = new AppFactory();
        var response = await AppFactory.RegisterAsync(factory.CreateBrowser(), "short@example.com", "elevenchars");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form re-rendered with errors
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task Login_posts_are_rate_limited_per_ip()
    {
        await using var factory = new AppFactory();
        var browser = factory.CreateBrowser();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await browser.PostAsync("/Account/Login", new FormUrlEncodedContent([]))).StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account/Login")).StatusCode); // GETs unaffected
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["RateLimiting:AuthPermitPerMinute"] = "100" });
        await AppFactory.RegisterAsync(factory.CreateBrowser(), "victim@example.com", Password);
        await TestUtil.ConfirmEmailAsync(factory, "victim@example.com");

        var browser = factory.CreateBrowser();
        for (var i = 0; i < 5; i++)
        {
            await AppFactory.LoginAsync(browser, "victim@example.com", "wrong password " + i);
        }
        var response = await AppFactory.LoginAsync(browser, "victim@example.com", Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Account/Lockout", response.Headers.Location!.ToString());
    }
}
