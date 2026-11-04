using System.Buffers.Binary;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
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

/// <summary>Phase 5a: setup key, DB settings, account states and setup links, mail outbox, verification, recovery, self-service, admin.</summary>
public class Phase5aTests
{
    private const string NewPassword = "another long passphrase";

    // ---- helpers --------------------------------------------------------------------------------

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

    private static Task<ApplicationUser> UserAsync(AppFactory factory, string id) =>
        WithScope(factory, async sp => Revealed(factory, await sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id)));

    private static Task<ApplicationUser?> UserByEmailAsync(AppFactory factory, string email) =>
        WithScope(factory, async sp => await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email) is { } u ? Revealed(factory, u) : null);

    private static Task<int> OutboxCountAsync(AppFactory factory) =>
        WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.CountAsync());

    private static Dictionary<string, string> PasswordFields(string password = Password) => new()
    {
        ["Input.Password"] = password,
        ["Input.ConfirmPassword"] = password,
    };

    private static string PathOf(string url) => new Uri(url).PathAndQuery;

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) from a base32 key, as an authenticator app computes it.</summary>
    internal static string Totp(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var key = new List<byte>();
        foreach (var c in base32Key.ToUpperInvariant().Where(ch => ch != ' ' && ch != '='))
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                key.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key.ToArray(), counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    internal static async Task<HttpResponseMessage> EnrolTotpAsync(AppFactory factory, HttpClient browser, string email)
    {
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AccountGate.EnrolPath)).StatusCode); // creates the key
        var key = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.GetAuthenticatorKeyAsync((await users.FindByEmailAsync(email))!))!;
        });
        return await AppFactory.SubmitFormAsync(browser, AccountGate.EnrolPath, "send-code", new Dictionary<string, string> { ["Input.Code"] = Totp(key) });
    }

    // ---- 1. setup key -----------------------------------------------------------------------------

    private static Dictionary<string, string> SetupFields(string key, string email) => new(PasswordFields())
    {
        ["Input.Key"] = key,
        ["Input.Email"] = email,
    };

    [Fact]
    public async Task Setup_key_creates_the_admin_once_and_the_session_is_gated_until_totp()
    {
        await using var factory = new AppFactory();
        Assert.True(factory.SetupKey.IsLive); // issued at startup: no admin yet
        var key = factory.SetupKey.Issue();
        var browser = factory.CreateBrowser();

        var wrong = await AppFactory.SubmitFormAsync(browser, "/setup", "setup-admin", SetupFields(SecretTokens.Generate().Token, "boss@example.com"));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Setup key is wrong", await wrong.Content.ReadAsStringAsync());
        Assert.Null(await UserByEmailAsync(factory, "boss@example.com"));

        var created = await AppFactory.SubmitFormAsync(browser, "/setup", "setup-admin", SetupFields(key, "boss@example.com"));
        Assert.True(created.StatusCode == HttpStatusCode.Redirect, await created.Content.ReadAsStringAsync());
        Assert.EndsWith(AccountGate.EnrolPath, created.Headers.Location!.ToString());
        var admin = (await UserByEmailAsync(factory, "boss@example.com"))!;
        Assert.True(admin.EmailConfirmed);
        Assert.True(admin.MustEnrolTwoFactor);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().IsInRoleAsync(admin, AppRoles.Admin)));

        // The key is dead and /setup is gone.
        Assert.False(factory.SetupKey.IsLive);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync("/setup")).StatusCode);
        var again = await AppFactory.SubmitFormAsync(factory.CreateBrowser(), "/setup", "setup-admin", SetupFields(key, "second@example.com"), tokenSourcePath: "/Account/Login");
        Assert.NotEqual(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Null(await UserByEmailAsync(factory, "second@example.com"));

        // Gate: everything but enrolment, logout and static files sends the admin to enrolment.
        foreach (var path in new[] { "/", "/polls", "/admin", "/Account/Manage", "/polls/x/export.csv" })
        {
            var gated = await browser.GetAsync(path);
            Assert.True(gated.StatusCode == HttpStatusCode.SeeOther, $"{path} -> {gated.StatusCode}");
            Assert.Equal(AccountGate.EnrolPath, gated.Headers.Location!.ToString());
        }
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/app.css")).StatusCode);

        var enrolled = await EnrolTotpAsync(factory, browser, "boss@example.com");
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        Assert.Contains("Recovery codes", await enrolled.Content.ReadAsStringAsync());
        Assert.False((await UserByEmailAsync(factory, "boss@example.com"))!.MustEnrolTwoFactor);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Setup_is_404_when_an_admin_exists_or_no_key_is_live()
    {
        await using var factory = new AppFactory();
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateBrowser().GetAsync("/setup")).StatusCode);
        factory.SetupKey.Revoke();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync("/setup")).StatusCode);

        factory.SetupKey.Issue();
        await AdminAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync("/setup")).StatusCode);
    }

    [Fact]
    public async Task Setup_posts_are_rate_limited()
    {
        await using var factory = new AppFactory();
        var browser = factory.CreateBrowser();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) statuses.Add((await browser.PostAsync("/setup", new FormUrlEncodedContent([]))).StatusCode);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(5));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
    }

    // ---- 2. settings ------------------------------------------------------------------------------

    [Fact]
    public async Task Registration_off_hides_the_link_and_404s_the_page()
    {
        await using var factory = new AppFactory(registrationOpen: false);
        var browser = factory.CreateBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Account/Register")).StatusCode);
        Assert.DoesNotContain("Account/Register", await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("Account/Register", await (await browser.GetAsync("/")).Content.ReadAsStringAsync());
        var forged = await AppFactory.SubmitFormAsync(browser, "/Account/Register", "register", new Dictionary<string, string>(PasswordFields())
        {
            ["Input.Email"] = "sneaky@example.com",
        }, tokenSourcePath: "/Account/Login");
        Assert.NotEqual(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Null(await UserByEmailAsync(factory, "sneaky@example.com"));

        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.RegistrationEnabled] = "true" }, null);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account/Register")).StatusCode);
        Assert.Contains("Account/Register", await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Settings_are_cached_secrets_are_protected_and_display_name_applies()
    {
        await using var factory = new AppFactory();
        Assert.Equal(100, factory.Settings.Current.Mail.HourlyCap);
        Assert.Equal(1000, factory.Settings.Current.Limits.MaxBallotsPerPoll);

        await factory.Settings.SetAsync(new Dictionary<string, string?>
        {
            [SettingKeys.MailPassword] = "hunter2-smtp-token",
            [SettingKeys.DisplayName] = "Club Vote",
            [SettingKeys.LimitBallotsPerPoll] = "50",
        }, "someone");

        var stored = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AppSettings.AsNoTracking().SingleAsync(s => s.Key == SettingKeys.MailPassword));
        Assert.DoesNotContain("hunter2", stored.Value);
        Assert.Equal("someone", stored.UpdatedBy);
        Assert.Equal("hunter2-smtp-token", factory.Settings.GetMailPassword());
        Assert.True(factory.Settings.Current.Mail.HasPassword);
        Assert.Equal(50, factory.Settings.Current.Limits.MaxBallotsPerPoll);

        var home = await (await factory.CreateBrowser().GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("Club Vote", home);

        // Blank = back to the default.
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.DisplayName] = "" }, null);
        Assert.Contains("Telrang", await (await factory.CreateBrowser().GetAsync("/")).Content.ReadAsStringAsync());
    }

    // ---- 3. account states and setup links --------------------------------------------------------

    [Fact]
    public async Task Admin_created_account_is_set_up_through_its_link_then_must_enrol_totp()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var outcome = await Admin(factory, a => a.CreateAccountAsync(admin, "https://localhost/"));
        Assert.True(outcome.Ok, outcome.Message);
        var path = PathOf(outcome.Link!);
        Assert.StartsWith("/setup/account/", path);

        var pending = Assert.Single((await Admin(factory, a => a.ListUsersAsync(admin)))!, u => u.PendingSetup);
        Assert.Null(pending.Email);

        var browser = factory.CreateBrowser();
        var done = await AppFactory.SubmitFormAsync(browser, path, "setup-account", new Dictionary<string, string>(PasswordFields())
        {
            ["Input.Email"] = "new@example.com",
        });
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.EndsWith(AccountGate.EnrolPath, done.Headers.Location!.ToString());

        var user = await UserAsync(factory, pending.Id);
        Assert.Equal(("new@example.com", "new@example.com", false, false, true),
            (user.Email, user.UserName, user.EmailConfirmed, user.PendingSetup, user.MustEnrolTwoFactor));
        Assert.Equal(HttpStatusCode.SeeOther, (await browser.GetAsync("/polls")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync(path)).StatusCode); // single use

        // No mail configured: the admin marks the address verified.
        Assert.True((await Admin(factory, a => a.MarkEmailVerifiedAsync(admin, pending.Id))).Ok);
        Assert.True((await UserAsync(factory, pending.Id)).EmailConfirmed);

        Assert.Equal(HttpStatusCode.OK, (await EnrolTotpAsync(factory, browser, "new@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/polls")).StatusCode);
    }

    [Fact]
    public async Task Invite_needs_mail_and_the_mailed_link_verifies_the_address()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        Assert.False((await Admin(factory, a => a.InviteAsync(admin, "inv@example.com"))).Ok);

        await ConfigureMailAsync(factory);
        Assert.True((await Admin(factory, a => a.InviteAsync(admin, "inv@example.com"))).Ok);
        Assert.Equal(1, await factory.SendMailAsync());
        var path = factory.Mail.LinkTo("inv@example.com");

        var browser = factory.CreateBrowser();
        Assert.Contains("inv@example.com", await (await browser.GetAsync(path)).Content.ReadAsStringAsync());
        var done = await AppFactory.SubmitFormAsync(browser, path, "setup-account", PasswordFields());
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        var user = (await UserByEmailAsync(factory, "inv@example.com"))!;
        Assert.True(user.EmailConfirmed);
        Assert.Equal("inv@example.com", user.UserName);
    }

    [Fact]
    public async Task Admin_reset_link_sets_a_password_keeps_2fa_and_clears_lockout()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var userId = await CreateUserAsync(factory, "user@example.com");
        var stampBefore = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(10));
            return await users.GetSecurityStampAsync(user);
        });

        var outcome = await Admin(factory, a => a.ResetPasswordAsync(admin, userId, "https://localhost/"));
        Assert.True(outcome.Ok);
        var path = PathOf(outcome.Link!);
        var browser = factory.CreateBrowser();
        var reset = await AppFactory.SubmitFormAsync(browser, path, "reset-password", PasswordFields(NewPassword));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains("ResetPasswordConfirmation", reset.Headers.Location!.ToString());

        var user = await UserAsync(factory, userId);
        Assert.True(user.TwoFactorEnabled);
        Assert.Null(user.LockoutEnd);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.True(await WithScope(factory, async sp => await sp.GetRequiredService<UserManager<ApplicationUser>>().CheckPasswordAsync((await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId))!, NewPassword)));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Admin_disable_2fa_forces_enrolment_at_next_login_and_login_is_recorded()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var userId = await CreateUserAsync(factory, "user@example.com");
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            return await users.SetTwoFactorEnabledAsync(user, true);
        });
        Assert.Null((await UserAsync(factory, userId)).LastLoginAt);

        Assert.True((await Admin(factory, a => a.DisableTwoFactorAsync(admin, userId))).Ok);
        var user = await UserAsync(factory, userId);
        Assert.True(user.MustEnrolTwoFactor);
        Assert.False(user.TwoFactorEnabled);

        var browser = await LoggedInBrowserAsync(factory, "user@example.com");
        Assert.NotNull((await UserAsync(factory, userId)).LastLoginAt);
        var gated = await browser.GetAsync("/polls");
        Assert.Equal(HttpStatusCode.SeeOther, gated.StatusCode);
        Assert.Equal(AccountGate.EnrolPath, gated.Headers.Location!.ToString());

        // Logout stays reachable while gated.
        var logout = await AppFactory.SubmitFormAsync(browser, "/Account/Logout", "logout", new Dictionary<string, string> { ["ReturnUrl"] = "" }, tokenSourcePath: AccountGate.EnrolPath);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/polls")).StatusCode); // now: login redirect
    }

    [Fact]
    public async Task Poll_creation_requires_a_verified_email()
    {
        await using var factory = new AppFactory();
        // An account set up from an admin's link (not self-registered): it may sign in before its email is verified.
        var userId = await WithScope(factory, async sp =>
        {
            var user = new ApplicationUser { UserName = "new@example.com", Email = "new@example.com" };
            Assert.True((await sp.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, Password)).Succeeded);
            return user.Id;
        });
        var browser = await LoggedInBrowserAsync(factory, "new@example.com");

        var refused = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(userId, RankedDraft()));
        Assert.Equal(PollSaveStatus.EmailNotVerified, refused.Status);
        // Mail is off here: no resend link that would dead-end.
        var page = await (await browser.GetAsync("/polls/new")).Content.ReadAsStringAsync();
        Assert.Contains("Ask an admin to verify it", page);
        Assert.DoesNotContain("Resend verification", page);
        await ConfigureMailAsync(factory);
        Assert.Contains("Resend verification", await (await browser.GetAsync("/polls/new")).Content.ReadAsStringAsync());

        await ConfirmEmailAsync(factory, "new@example.com");
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(userId, RankedDraft()))).Succeeded);
        Assert.DoesNotContain("Resend verification", await (await browser.GetAsync("/polls/new")).Content.ReadAsStringAsync());
    }

    // ---- 4. mail outbox -----------------------------------------------------------------------------

    [Fact]
    public async Task Outbox_drops_within_cooldown_and_over_the_daily_maximum()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var queue = factory.Services.GetRequiredService<IMailQueue>();

        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.Verification);
        Assert.Equal(0, await OutboxCountAsync(factory)); // mail not configured: dropped

        await ConfigureMailAsync(factory);
        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.Verification);
        await queue.EnqueueAsync("A@Example.test", "s", "t", MailType.Verification); // same recipient, same type, in cooldown
        Assert.Equal(1, await OutboxCountAsync(factory));
        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.PasswordReset); // other type: own cooldown
        Assert.Equal(2, await OutboxCountAsync(factory));

        // The daily maximum (3) counts per type: verification mails cannot use up
        // the quota of reset mails.
        clock.Now += TimeSpan.FromMinutes(16);
        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.Verification);
        clock.Now += TimeSpan.FromMinutes(16);
        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.Verification);
        Assert.Equal(4, await OutboxCountAsync(factory));
        clock.Now += TimeSpan.FromMinutes(16);
        var over = await queue.TryEnqueueAsync("a@example.test", "s", "t", MailType.Verification); // 4th verification in 24 h
        Assert.Equal((false, MailDropReason.DailyMax), (over.Queued, over.Reason));
        Assert.Equal(4, await OutboxCountAsync(factory));
        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.PasswordReset);
        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.EmailChange);
        Assert.Equal(6, await OutboxCountAsync(factory)); // other types unaffected
        var cooling = await queue.TryEnqueueAsync("a@example.test", "s", "t", MailType.EmailChange);
        Assert.Equal((false, MailDropReason.Cooldown), (cooling.Queued, cooling.Reason));
        Assert.InRange(cooling.RetryAfter!.Value, TimeSpan.FromMinutes(14.9), TimeSpan.FromMinutes(15));
        Assert.Equal("Already sent recently. Try again in 15 minutes.", cooling.DroppedMessage);
        await queue.EnqueueAsync("b@example.test", "s", "t", MailType.Verification); // other recipients unaffected
        Assert.Equal(7, await OutboxCountAsync(factory));

        clock.Now += TimeSpan.FromHours(24);
        await queue.EnqueueAsync("a@example.test", "s", "t", MailType.Verification);
        Assert.Equal(8, await OutboxCountAsync(factory));
    }

    [Fact]
    public async Task Sender_honours_the_hourly_cap()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAsync(factory, hourlyCap: 2);
        var queue = factory.Services.GetRequiredService<IMailQueue>();
        foreach (var to in new[] { "r1@example.test", "r2@example.test", "r3@example.test" })
            await queue.EnqueueAsync(to, "s", "t", MailType.Verification);

        Assert.Equal(2, await factory.SendMailAsync());
        Assert.Equal(0, await factory.SendMailAsync());
        clock.Now += TimeSpan.FromMinutes(30);
        Assert.Equal(0, await factory.SendMailAsync());
        clock.Now += TimeSpan.FromMinutes(31);
        Assert.Equal(1, await factory.SendMailAsync());
        Assert.Equal(3, factory.Mail.Sent.Select(m => m.To).Distinct().Count());
    }

    [Fact]
    public async Task Failed_mail_retries_with_backoff_then_dead_letters()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAsync(factory);
        factory.Mail.Fail = true;
        await factory.Services.GetRequiredService<IMailQueue>().EnqueueAsync("a@example.test", "s", "t", MailType.Verification);

        Task<OutboxMessage> Row() => WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking().SingleAsync());

        Assert.Equal(0, await factory.SendMailAsync());
        Assert.Equal(1, (await Row()).Attempts);
        // Stored as a long (DateTimeOffsetToBinaryConverter keeps 0.1 ms precision).
        Assert.InRange((await Row()).SendAfter, clock.Now + TimeSpan.FromMinutes(1) - TimeSpan.FromMilliseconds(1), clock.Now + TimeSpan.FromMinutes(1));
        await factory.SendMailAsync(); // not due yet
        Assert.Equal(1, (await Row()).Attempts);

        foreach (var (wait, attempts) in new[] { (1, 2), (5, 3), (15, 4) })
        {
            clock.Now += TimeSpan.FromMinutes(wait) - TimeSpan.FromSeconds(1);
            await factory.SendMailAsync();
            Assert.Equal(attempts - 1, (await Row()).Attempts); // one second early: still waiting
            clock.Now += TimeSpan.FromSeconds(1);
            await factory.SendMailAsync();
            Assert.Equal(attempts, (await Row()).Attempts);
        }
        clock.Now += TimeSpan.FromMinutes(60);
        await factory.SendMailAsync();
        var dead = await Row();
        Assert.Equal(5, dead.Attempts);
        Assert.NotNull(dead.DeadAt);
        Assert.Contains("SMTP is down", dead.LastError);

        factory.Mail.Fail = false;
        clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(0, await factory.SendMailAsync()); // dead letters are not retried
        var stats = await factory.Services.GetRequiredService<MailSender>().GetStatsAsync();
        Assert.Equal((0, 1), (stats.Queued, stats.Dead));
        Assert.Contains("SMTP is down", stats.LastError);
    }

    [Fact]
    public async Task Failed_mail_is_sent_on_a_later_attempt()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAsync(factory);
        factory.Mail.Fail = true;
        await factory.Services.GetRequiredService<IMailQueue>().EnqueueAsync("a@example.test", "Hello", "Body", MailType.Test);
        await factory.SendMailAsync();
        factory.Mail.Fail = false;
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, await factory.SendMailAsync());
        Assert.Equal("Hello", Assert.Single(factory.Mail.Sent).Subject);
        var row = Assert.Single(await factory.Services.GetRequiredService<MailSender>().RecentAsync());
        Assert.Equal("Sent", row.State);
    }

    // ---- 5. email verification and change -------------------------------------------------------------

    [Fact]
    public async Task Registration_mails_a_link_that_verifies_and_resend_respects_the_cooldown()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAsync(factory);
        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.RegisterAsync(browser, "v@example.com", Password)).StatusCode);
        Assert.Equal(1, await OutboxCountAsync(factory));

        // Resend inside the cooldown: nothing new is queued. An unverified self-registered account cannot sign in
        // (pentest R2-ACC-3), so this is the anonymous resend page, which answers the same either way.
        var resend = new Dictionary<string, string> { ["Input.Email"] = "v@example.com" };
        await AppFactory.SubmitFormAsync(browser, "/Account/ResendEmailConfirmation", "resend-email-confirmation", resend);
        Assert.Equal(1, await OutboxCountAsync(factory));
        clock.Now += TimeSpan.FromMinutes(16);
        await AppFactory.SubmitFormAsync(browser, "/Account/ResendEmailConfirmation", "resend-email-confirmation", resend);
        Assert.Equal(2, await OutboxCountAsync(factory));

        await factory.SendMailAsync();
        var path = factory.Mail.LinkTo("v@example.com");
        Assert.StartsWith("/Account/ConfirmEmail?", path);
        // GET shows the button, the POST with the account's password confirms (pentest R2-ACC-1).
        var confirmed = await AppFactory.SubmitFormAsync(factory.CreateBrowser(), path, "confirm-email", new Dictionary<string, string> { ["Password"] = Password });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Contains("Email confirmed", await confirmed.Content.ReadAsStringAsync());
        Assert.True((await UserByEmailAsync(factory, "v@example.com"))!.EmailConfirmed);
    }

    [Fact]
    public async Task Email_change_switches_only_after_the_new_address_confirms_and_notifies_the_old()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory);
        var userId = await CreateUserAsync(factory, "old@example.com");
        var browser = await LoggedInBrowserAsync(factory, "old@example.com");

        var requested = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/Email", "change-email", new Dictionary<string, string> { ["Input.NewEmail"] = "new@example.com", ["Input.Secret"] = Password });
        Assert.Contains("Confirmation link sent", await requested.Content.ReadAsStringAsync());
        Assert.Equal("old@example.com", (await UserAsync(factory, userId)).Email);

        await factory.SendMailAsync();
        var confirmed = await AppFactory.SubmitFormAsync(browser, factory.Mail.LinkTo("new@example.com"), "confirm-email-change", new Dictionary<string, string>());
        Assert.Contains("new@example.com", await confirmed.Content.ReadAsStringAsync());
        var user = await UserAsync(factory, userId);
        Assert.Equal(("new@example.com", "new@example.com", true), (user.Email, user.UserName, user.EmailConfirmed));

        await factory.SendMailAsync();
        Assert.Contains(factory.Mail.Sent, m => m.To == "old@example.com" && m.TextBody.Contains("new@example.com"));
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(factory.CreateBrowser(), "new@example.com", Password)).StatusCode);

        // An address that belongs to someone else: same answer, nothing sent.
        await CreateUserAsync(factory, "taken@example.com");
        var before = await OutboxCountAsync(factory);
        var taken = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/Email", "change-email", new Dictionary<string, string> { ["Input.NewEmail"] = "taken@example.com", ["Input.Secret"] = Password });
        Assert.Contains("Confirmation link sent", await taken.Content.ReadAsStringAsync());
        Assert.Equal(before, await OutboxCountAsync(factory));
    }

    // ---- 6. password recovery -------------------------------------------------------------------------

    private static Task<HttpResponseMessage> ForgotAsync(AppFactory factory, string email) =>
        AppFactory.SubmitFormAsync(factory.CreateBrowser(), "/Account/ForgotPassword", "forgot-password", new Dictionary<string, string> { ["Input.Email"] = email });

    [Fact]
    public async Task Forgot_password_answers_the_same_for_every_address_and_mails_only_verified_accounts()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory);
        await CreateUserAsync(factory, "known@example.com");
        var unverifiedBrowser = factory.CreateBrowser();
        await AppFactory.RegisterAsync(unverifiedBrowser, "unverified@example.com", Password);
        await factory.SendMailAsync(); // the registration's verification mail
        var sentBefore = factory.Mail.Sent.Count;

        var responses = new List<(HttpStatusCode, string?)>();
        foreach (var email in new[] { "known@example.com", "nobody@example.com", "unverified@example.com" })
        {
            var r = await ForgotAsync(factory, email);
            responses.Add((r.StatusCode, r.Headers.Location?.ToString()));
        }
        Assert.All(responses, r => Assert.Equal(responses[0], r));
        Assert.Equal(HttpStatusCode.Redirect, responses[0].Item1);
        Assert.Contains("ForgotPasswordConfirmation", responses[0].Item2);

        await factory.SendMailAsync();
        var mail = Assert.Single(factory.Mail.Sent.Skip(sentBefore));
        Assert.Equal("known@example.com", mail.To);
    }

    [Fact]
    public async Task Reset_link_works_once()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory);
        await CreateUserAsync(factory, "known@example.com");
        await ForgotAsync(factory, "known@example.com");
        await factory.SendMailAsync();
        var path = factory.Mail.LinkTo("known@example.com");
        Assert.StartsWith("/Account/ResetPassword?token=", path);

        var first = await AppFactory.SubmitFormAsync(factory.CreateBrowser(), path, "reset-password", PasswordFields(NewPassword));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(factory.CreateBrowser(), "known@example.com", NewPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync(path)).StatusCode);
        var second = await AppFactory.SubmitFormAsync(factory.CreateBrowser(), path, "reset-password", PasswordFields("a third long passphrase"), tokenSourcePath: "/Account/Login");
        Assert.NotEqual(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(factory.CreateBrowser(), "known@example.com", NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Reset_link_dies_after_a_password_change_and_requests_have_a_cooldown()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory);
        var userId = await CreateUserAsync(factory, "known@example.com");
        await ForgotAsync(factory, "known@example.com");
        await ForgotAsync(factory, "known@example.com"); // within 15 min: no second link
        Assert.Equal(1, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().SetupLinks.CountAsync(l => l.UserId == userId)));
        await factory.SendMailAsync();
        var path = factory.Mail.LinkTo("known@example.com");
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateBrowser().GetAsync(path)).StatusCode);

        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.ChangePasswordAsync((await users.FindByIdAsync(userId))!, Password, NewPassword);
        });
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync(path)).StatusCode);
    }

    // ---- 7. self-service --------------------------------------------------------------------------------

    [Fact]
    public async Task Deleted_account_cannot_log_in_and_its_email_can_register_again()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "gone@example.com");
        var pollId = await CreatePollAsync(factory, userId, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "gone@example.com");

        var unticked = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/PersonalData", "delete-account", new Dictionary<string, string>());
        Assert.Contains("Tick the box", await unticked.Content.ReadAsStringAsync());
        Assert.Null((await UserAsync(factory, userId)).DeletedAt);

        var deleted = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/PersonalData", "delete-account", new Dictionary<string, string> { ["ConfirmDelete"] = "true", ["Secret"] = Password });
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        var row = await UserAsync(factory, userId);
        Assert.NotNull(row.DeletedAt);
        Assert.Equal((null, userId, null, false), (row.Email, row.UserName, row.PasswordHash, row.TwoFactorEnabled));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync($"/p/{pollId}")).StatusCode);

        var login = await AppFactory.LoginAsync(factory.CreateBrowser(), "gone@example.com", Password);
        Assert.Contains("Invalid login attempt", await login.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/polls")).StatusCode); // old session is gone

        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.RegisterAsync(factory.CreateBrowser(), "gone@example.com", Password)).StatusCode);
        Assert.NotEqual(userId, (await UserByEmailAsync(factory, "gone@example.com"))!.Id);
    }

    [Fact]
    public async Task The_last_admin_cannot_delete_themselves()
    {
        await using var factory = new AppFactory();
        var (adminId, _) = await AdminAsync(factory);
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var refused = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/PersonalData", "delete-account", new Dictionary<string, string> { ["ConfirmDelete"] = "true", ["Secret"] = Password });
        Assert.Contains("last admin", await refused.Content.ReadAsStringAsync());
        Assert.Null((await UserAsync(factory, adminId)).DeletedAt);
    }

    [Fact]
    public async Task Download_my_data_returns_profile_and_poll_definitions()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "me@example.com");
        await CreatePollAsync(factory, userId, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "me@example.com");
        var token = AppFactory.ScrapeAntiforgeryToken(await (await browser.GetAsync("/Account/Manage/PersonalData")).Content.ReadAsStringAsync());

        // Re-authentication (finding 10): wrong or missing password goes back to the page.
        var wrong = await browser.PostAsync("/Account/Manage/DownloadPersonalData", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Secret"] = "nope" }));
        Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
        Assert.Contains("Password or code is wrong", await (await browser.GetAsync(wrong.Headers.Location)).Content.ReadAsStringAsync());

        var response = await browser.PostAsync("/Account/Manage/DownloadPersonalData", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Secret"] = Password }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("me@example.com", json.RootElement.GetProperty("profile").GetProperty("email").GetString());
        Assert.Equal("Trip", json.RootElement.GetProperty("polls")[0].GetProperty("definition").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("ballots").ValueKind);

        // Without the antiforgery token: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Account/Manage/DownloadPersonalData", new FormUrlEncodedContent([]))).StatusCode);
    }

    [Fact]
    public async Task Log_out_everywhere_ends_other_sessions()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await CreateUserAsync(factory, "me@example.com");
        var here = await LoggedInBrowserAsync(factory, "me@example.com");
        var elsewhere = await LoggedInBrowserAsync(factory, "me@example.com");
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync("/polls")).StatusCode);

        var token = AppFactory.ScrapeAntiforgeryToken(await (await here.GetAsync("/Account/Manage/PersonalData")).Content.ReadAsStringAsync());
        var done = await here.PostAsync("/Account/Manage/LogOutEverywhere", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await here.GetAsync("/polls")).StatusCode);

        clock.Now += TimeSpan.FromMinutes(2); // past the security-stamp validation interval
        var other = await elsewhere.GetAsync("/polls");
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
        Assert.Contains("/Account/Login", other.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Profile_saves_display_name_and_a_known_time_zone()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "me@example.com");
        var browser = await LoggedInBrowserAsync(factory, "me@example.com");
        var page = await (await browser.GetAsync("/Account/Manage")).Content.ReadAsStringAsync();
        Assert.Contains("data-default-tz", page);
        Assert.Contains("value=\"Europe/Berlin\"", page);

        var saved = await AppFactory.SubmitFormAsync(browser, "/Account/Manage", "profile", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Ann",
            ["Input.TimeZone"] = "Europe/Berlin",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(("Ann", "Europe/Berlin"), ((await UserAsync(factory, userId)).DisplayName, (await UserAsync(factory, userId)).TimeZone));

        await AppFactory.SubmitFormAsync(browser, "/Account/Manage", "profile", new Dictionary<string, string> { ["Input.TimeZone"] = "Mars/Olympus_Mons" });
        Assert.Equal("Europe/Berlin", (await UserAsync(factory, userId)).TimeZone);
    }

    // ---- 8. admin ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_actions_are_logged_newest_first_and_the_log_rotates()
    {
        await using var factory = new AppFactory();
        var (adminId, admin) = await AdminAsync(factory);
        var userId = await CreateUserAsync(factory, "user@example.com");
        Assert.True((await Admin(factory, a => a.SetAdminAsync(admin, userId, true))).Ok);
        Assert.True((await Admin(factory, a => a.LockAsync(admin, userId))).Ok);

        var (rows, total) = (await Admin(factory, a => a.ListLogAsync(admin, 0)))!.Value;
        Assert.Equal(2, total);
        Assert.Equal(["user.lock", "user.promote"], rows.Select(r => r.Action));
        Assert.Equal(("admin@example.com", "user", userId), (rows[0].AdminEmail, rows[0].TargetType, rows[0].TargetId));

        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            db.AdminActionLogs.AddRange(Enumerable.Range(0, AdminLog.MaxRows).Select(i => new AdminActionLog { At = DateTimeOffset.UtcNow, AdminUserId = adminId, Action = "bulk" }));
            await db.SaveChangesAsync();
            await AdminLog.WriteAsync(db, TimeProvider.System, sp.GetRequiredService<EmailProtection>(), adminId, "latest");
            Assert.Equal(AdminLog.MaxRows, await db.AdminActionLogs.CountAsync());
            Assert.False(await db.AdminActionLogs.AnyAsync(l => l.Action == "user.promote")); // oldest went first
            return true;
        });
        var page2 = (await Admin(factory, a => a.ListLogAsync(admin, 1)))!.Value;
        Assert.Equal(AdminLog.PageSize, page2.Rows.Count);
        Assert.Equal("latest", (await Admin(factory, a => a.ListLogAsync(admin, 0)))!.Value.Rows[0].Action);
    }

    [SqliteOnlyFact] // PostgreSQL: PostgresTests.Backup_tab_points_to_pg_dump_and_the_download_is_gone
    public async Task Admin_tabs_render_and_backup_streams_a_rate_limited_sqlite_file()
    {
        await using var factory = new AppFactory();
        await AdminAsync(factory);
        await CreateUserAsync(factory, "user@example.com");
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        foreach (var tab in new[] { "", "?tab=polls", "?tab=settings", "?tab=mail", "?tab=log" })
        {
            var response = await browser.GetAsync("/admin" + tab);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{tab} -> {response.StatusCode}");
        }
        var settingsHtml = await (await browser.GetAsync("/admin?tab=settings")).Content.ReadAsStringAsync();
        Assert.Contains("action=\"admin/backup\"", settingsHtml);
        var token = AppFactory.ScrapeAntiforgeryToken(settingsHtml);

        var backup = await browser.PostAsync("/admin/backup", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        var bytes = await backup.Content.ReadAsByteArrayAsync();
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(bytes, 0, 16));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.PostAsync("/admin/backup", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }))).StatusCode);

        var user = await LoggedInBrowserAsync(factory, "user@example.com");
        var userToken = AppFactory.ScrapeAntiforgeryToken(await (await user.GetAsync("/Account/Manage/PersonalData")).Content.ReadAsStringAsync());
        var denied = await user.PostAsync("/admin/backup", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = userToken }));
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
    }

    [Fact]
    public async Task About_page_renders_markdown_and_is_linked_only_when_set()
    {
        await using var factory = new AppFactory();
        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/about")).StatusCode);
        Assert.DoesNotContain("href=\"about\"", await (await browser.GetAsync("/")).Content.ReadAsStringAsync());

        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LegalPageMarkdown] = "# Privacy\n\nWe keep **cookies** only. <script>x</script>" }, null);
        var about = await (await browser.GetAsync("/about")).Content.ReadAsStringAsync();
        Assert.Contains("<strong>cookies</strong>", about);
        Assert.DoesNotContain("<script>x", about);
        Assert.Contains("href=\"about\"", await (await browser.GetAsync("/")).Content.ReadAsStringAsync());
    }
}
