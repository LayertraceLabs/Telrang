using System.Net;
using System.Security.Claims;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN §22.1: "Auto-verify created accounts" while mail is not configured.</summary>
public class AutoVerifyTests
{
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

    private static Task SetAutoVerifyAsync(AppFactory factory, bool on) =>
        factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.AutoVerifyWithoutMail] = on ? "true" : null }, null);

    /// <summary>Admin "Create account", then the link is completed with <paramref name="email"/> and TOTP enrolled.</summary>
    private static async Task<(ApplicationUser User, HttpClient Browser)> CreateAndSetUpAsync(AppFactory factory, string email)
    {
        var admin = await AdminAsync(factory);
        var outcome = await WithScope(factory, sp => sp.GetRequiredService<AdminService>().CreateAccountAsync(admin, "https://localhost/"));
        Assert.True(outcome.Ok, outcome.Message);
        var browser = factory.CreateBrowser();
        var done = await AppFactory.SubmitFormAsync(browser, new Uri(outcome.Link!).PathAndQuery, "setup-account", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
        });
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Phase5aTests.EnrolTotpAsync(factory, browser, email)).StatusCode);
        var hash = EmailHash(factory, email);
        var user = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == hash));
        return (user, browser);
    }

    private static Task<PollSaveResult> TryCreatePollAsync(AppFactory factory, string userId) =>
        WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(userId, RankedDraft()));

    private static async Task<List<OutboxMessage>> VerificationMailsAsync(AppFactory factory, string to)
    {
        var hash = EmailHash(factory, to);
        return Revealed(factory, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.ToHash == hash && m.Type == MailType.Verification).ToListAsync()));
    }

    [Fact]
    public async Task Off_and_mail_off_leaves_the_created_account_unverified()
    {
        await using var factory = new AppFactory();
        var (user, _) = await CreateAndSetUpAsync(factory, "new@example.com");
        Assert.False(user.EmailConfirmed);
        Assert.Equal(PollSaveStatus.EmailNotVerified, (await TryCreatePollAsync(factory, user.Id)).Status);
    }

    [Fact]
    public async Task On_and_mail_off_verifies_the_created_account_without_mail()
    {
        await using var factory = new AppFactory();
        await SetAutoVerifyAsync(factory, true);
        var (user, _) = await CreateAndSetUpAsync(factory, "new@example.com");
        Assert.True(user.EmailConfirmed);
        Assert.Empty(await VerificationMailsAsync(factory, "new@example.com"));
        Assert.Equal(0, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.CountAsync()));
        Assert.True((await TryCreatePollAsync(factory, user.Id)).Succeeded);
    }

    [Fact]
    public async Task On_and_mail_configured_keeps_the_verification_mail()
    {
        await using var factory = new AppFactory();
        await SetAutoVerifyAsync(factory, true);
        await ConfigureMailAsync(factory);
        var (user, _) = await CreateAndSetUpAsync(factory, "new@example.com");
        Assert.False(user.EmailConfirmed);
        Assert.Single(await VerificationMailsAsync(factory, "new@example.com"));
        Assert.Equal(PollSaveStatus.EmailNotVerified, (await TryCreatePollAsync(factory, user.Id)).Status);
    }

    [Fact]
    public async Task Setting_is_read_when_the_link_is_completed()
    {
        await using var factory = new AppFactory();
        var admin = await AdminAsync(factory);
        var outcome = await WithScope(factory, sp => sp.GetRequiredService<AdminService>().CreateAccountAsync(admin, "https://localhost/"));
        await SetAutoVerifyAsync(factory, true); // after the link was issued
        var done = await AppFactory.SubmitFormAsync(factory.CreateBrowser(), new Uri(outcome.Link!).PathAndQuery, "setup-account", new Dictionary<string, string>
        {
            ["Input.Email"] = "late@example.com",
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
        });
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("late@example.com")))!.EmailConfirmed);
    }

    [Fact]
    public async Task Self_registration_is_not_affected()
    {
        await using var factory = new AppFactory();
        await SetAutoVerifyAsync(factory, true);
        var registered = await AppFactory.RegisterAsync(factory.CreateBrowser(), "self@example.com", Password);
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        var user = await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("self@example.com"));
        Assert.False(user!.EmailConfirmed);
    }
}
