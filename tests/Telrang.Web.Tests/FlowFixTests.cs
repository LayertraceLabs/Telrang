using System.Net;
using System.Reflection;
using System.Security.Claims;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Fixes from the user-flow review (fix round of 2026-10-26).</summary>
public class FlowFixTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], []);

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task SetMustEnrolAsync(AppFactory factory, string userId)
    {
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            user.MustEnrolTwoFactor = true;
            return (await users.UpdateAsync(user)).Succeeded;
        });
    }

    private static Task<ClaimsPrincipal> PrincipalAsync(AppFactory factory, string userId) =>
        WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync((await users.FindByIdAsync(userId))!);
        });

    private static async Task<string> AddAdminRoleAsync(AppFactory factory, string userId)
    {
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.AddToRoleAsync((await users.FindByIdAsync(userId))!, AppRoles.Admin)).Succeeded;
        });
        return userId;
    }

    /// <summary>What <c>AuthorizeRouteView</c> does on every render (SSR and interactive): combine the page's authorize data and evaluate it.</summary>
    private static Task<bool> PageAuthorizesAsync(AppFactory factory, Type page, ClaimsPrincipal user) =>
        WithScope(factory, async sp =>
        {
            if (page.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any()) return true;
            var data = page.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToArray();
            if (data.Length == 0) return true;
            var policy = (await AuthorizationPolicy.CombineAsync(sp.GetRequiredService<IAuthorizationPolicyProvider>(), data))!;
            return (await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy)).Succeeded;
        });

    private static HttpClient CookielessClient(AppFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
    });

    // ---- 1. account gate --------------------------------------------------------------------------

    [Fact]
    public async Task Gated_user_is_sent_to_enrolment_everywhere_over_http_and_reaches_polls_after_enrolment()
    {
        await using var factory = new AppFactory();
        var userId = await AddAdminRoleAsync(factory, await CreateUserAsync(factory, "gated@example.com"));
        var pollId = await CreatePollAsync(factory, userId, RankedDraft());
        await SetMustEnrolAsync(factory, userId);
        var browser = await LoggedInBrowserAsync(factory, "gated@example.com");

        foreach (var path in new[] { "/admin", "/polls", "/polls/new", $"/polls/{pollId}/edit", $"/polls/{pollId}/export.csv", "/not-found", "/about", "/Error", $"/p/{pollId}" })
        {
            var gated = await browser.GetAsync(path);
            Assert.True(gated.StatusCode == HttpStatusCode.SeeOther, $"{path} -> {gated.StatusCode}");
            Assert.Equal(AccountGate.EnrolPath, gated.Headers.Location!.ToString());
        }

        // Backup (POST) and the Blazor hub (no circuit for a gated session) are refused the same way.
        var backup = await browser.PostAsync("/admin/backup", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.SeeOther, backup.StatusCode);
        Assert.Equal(AccountGate.EnrolPath, backup.Headers.Location!.ToString());
        var negotiate = await browser.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.SeeOther, negotiate.StatusCode);

        // The enrolment page and its assets stay reachable.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/app.css")).StatusCode);
        var enrolled = await Phase5aTests.EnrolTotpAsync(factory, browser, "gated@example.com");
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        Assert.Contains("Recovery codes", await enrolled.Content.ReadAsStringAsync());

        // Same browser, no new login: the refreshed cookie has no gate claim.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/polls")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/polls/{pollId}/export.csv")).StatusCode);
    }

    [Fact]
    public async Task Page_policies_refuse_a_gated_principal_as_interactive_routing_evaluates_them()
    {
        await using var factory = new AppFactory();
        var userId = await AddAdminRoleAsync(factory, await CreateUserAsync(factory, "gated@example.com"));
        var free = await PrincipalAsync(factory, userId);
        await SetMustEnrolAsync(factory, userId);
        var gated = await PrincipalAsync(factory, userId);
        Assert.True(AccountGate.IsGated(gated));
        Assert.False(AccountGate.IsGated(free));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var protectedPages = new[]
        {
            typeof(AdminPage), typeof(MyPolls), typeof(NewPoll), typeof(EditPoll), typeof(SharePoll),
            typeof(Components.Pages.Home), typeof(About), typeof(Components.Account.Pages.Manage.Index),
        };
        foreach (var page in protectedPages)
        {
            Assert.False(await PageAuthorizesAsync(factory, page, gated), page.Name);
            Assert.True(await PageAuthorizesAsync(factory, page, free), page.Name);
        }
        Assert.True(await PageAuthorizesAsync(factory, typeof(Components.Pages.Home), anonymous));
        Assert.True(await PageAuthorizesAsync(factory, typeof(Components.Account.Pages.Manage.EnableAuthenticator), gated));
    }

    [Fact]
    public void Every_interactively_routable_page_declares_its_authorization()
    {
        // Pages without metadata are only gated over HTTP (fallback policy). Interactive navigation
        // skips HTTP, so every page that can render in a circuit must say what it allows.
        var allowedWithout = new HashSet<Type> { typeof(Components.Pages.NotFound), typeof(Components.Pages.Error) };
        var missing = typeof(AccountGate).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
            .Where(t => !t.GetCustomAttributes(inherit: true).OfType<ExcludeFromInteractiveRoutingAttribute>().Any())
            .Where(t => !t.GetCustomAttributes(inherit: true).Any(a => a is IAuthorizeData or IAllowAnonymous))
            .Where(t => !allowedWithout.Contains(t))
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public async Task Services_refuse_a_gated_account_even_with_a_stale_principal()
    {
        await using var factory = new AppFactory();
        var userId = await AddAdminRoleAsync(factory, await CreateUserAsync(factory, "gated@example.com"));
        var pollId = await CreatePollAsync(factory, userId, RankedDraft());
        var stale = await PrincipalAsync(factory, userId); // issued before the account was gated: no claim
        await SetMustEnrolAsync(factory, userId);
        var gated = await PrincipalAsync(factory, userId);

        foreach (var principal in new[] { stale, gated })
        {
            Assert.False(await WithScope(factory, sp => sp.GetRequiredService<AdminService>().IsAdminAsync(principal)));
            Assert.Null(await WithScope(factory, sp => sp.GetRequiredService<AdminService>().ListUsersAsync(principal)));
        }

        var created = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(userId, RankedDraft()));
        Assert.Equal(PollSaveStatus.SetupRequired, created.Status);
        var updated = await WithScope(factory, sp => sp.GetRequiredService<PollService>()
            .UpdatePollAsync(userId, pollId, RankedDraft()));
        Assert.Equal(PollSaveStatus.SetupRequired, updated.Status);

        var submit = await SubmitRankedAsync(factory, pollId, new VisitorContext(userId, "voter-key", null), ParisFirst);
        Assert.Equal(SubmitStatus.Denied, submit.Status);
        Assert.Empty(await BallotsAsync(factory, pollId));
    }

    // ---- 2. cookies blocked -----------------------------------------------------------------------

    [Fact]
    public async Task Without_a_cookie_jar_the_vote_page_renders_no_vote_form()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var curl = CookielessClient(factory);

        for (var visit = 0; visit < 2; visit++)
        {
            var first = await curl.GetAsync($"/p/{id}");
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
            Assert.EndsWith($"/p/{id}?ck=1", first.Headers.Location!.ToString());
            Assert.Contains(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith("vk="));

            // Back without the cookie: no second redirect (no loop), options visible, no ballot editor.
            var second = await curl.GetAsync(first.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var html = await second.Content.ReadAsStringAsync();
            Assert.Contains("Cookies are required to vote.", html);
            Assert.Contains("Reload", html);
            Assert.Contains("Paris", html);
            Assert.DoesNotContain("Your ranking", html);
            Assert.DoesNotContain("_handler", html);
        }

        // Without a vk the service refuses too, so no ballot can be created.
        var refused = await SubmitRankedAsync(factory, id, new VisitorContext(null, null, null), ParisFirst);
        Assert.Equal(SubmitStatus.Denied, refused.Status);
        Assert.Empty(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task With_a_cookie_jar_the_second_request_can_vote()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var (browser, jar) = factory.CreateBrowserWithJar();

        var first = await browser.GetAsync($"/p/{id}");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var check = await browser.GetAsync(first.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, check.StatusCode); // cookie came back: marker dropped
        Assert.EndsWith($"/p/{id}", check.Headers.Location!.ToString());
        var page = await browser.GetAsync(check.Headers.Location);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Your ranking", html);
        Assert.DoesNotContain("Cookies are required", html);

        Assert.True((await SubmitRankedAsync(factory, id, VisitorFrom(factory, jar, id), ParisFirst)).Succeeded);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Closed_poll_without_a_cookie_says_closed_and_does_not_redirect()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));

        var page = await CookielessClient(factory).GetAsync($"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Poll closed.", html);
        Assert.DoesNotContain("Cookies are required", html);
    }

    // ---- 3. voters: fixed at creation (DESIGN.md §26) ------------------------------------------------

    [Theory]
    [InlineData(VoterMode.Public, VoterMode.Invite)]
    [InlineData(VoterMode.Invite, VoterMode.Public)]
    public async Task Who_votes_cannot_change_with_or_without_ballots(VoterMode from, VoterMode to)
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: from));
        var refused = await WithScope(factory, sp => sp.GetRequiredService<PollService>().UpdatePollAsync(owner, id, RankedDraft(voters: to)));
        Assert.Equal(PollSaveStatus.Invalid, refused.Status);
        Assert.Contains(new DraftError("voters", PollDraftValidator.FixedMessage("Who votes")), refused.Errors);
        var model = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id));
        Assert.Equal(from, model!.Definition.Voters);
    }

    [Fact]
    public async Task Editing_an_invite_poll_keeps_its_links_linked()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var invite = Assert.Single((await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, [])))!);
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, null, hash), ParisFirst)).Succeeded);

        var saved = await WithScope(factory, sp => sp.GetRequiredService<PollService>()
            .UpdatePollAsync(owner, id, RankedDraft(voters: VoterMode.Invite).Q(q => q.Options.Add(new OptionDraft { Label = "Lisbon" }))));
        Assert.True(saved.Succeeded);
        Assert.Single(await BallotsAsync(factory, id));
        Assert.True(Assert.Single((await WithScope(factory, sp => sp.GetRequiredService<PollService>().ListInvitesAsync(owner, id)))!).Used);
    }

    // ---- 4. reset-link cooldown ---------------------------------------------------------------------

    private static async Task<(AppFactory Factory, MutableTime Clock, ApplicationUser User, string AdminId)> ResetSetupAsync(int mailCooldownMinutes)
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAsync(factory, cooldownMinutes: mailCooldownMinutes, dailyMax: 10);
        var adminId = await CreateUserAsync(factory, "admin@example.com");
        var userId = await CreateUserAsync(factory, "user@example.com");
        var user = await WithScope(factory, async sp => (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId))!);
        return (factory, clock, user, adminId);
    }

    private static Task<string> AdminResetAsync(AppFactory factory, ApplicationUser user, string adminId) =>
        WithScope(factory, async sp =>
        {
            var url = await sp.GetRequiredService<AccountService>().IssueAdminResetAsync(user, adminId, "https://localhost/");
            return url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        });

    private static Task<bool> LinkLiveAsync(AppFactory factory, string token) =>
        WithScope(factory, async sp => await sp.GetRequiredService<AccountService>().FindLinkAsync(token, [SetupLinkPurpose.PasswordReset]) is not null);

    private static Task<List<SetupLink>> ResetLinksAsync(AppFactory factory, string userId) =>
        WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().SetupLinks.AsNoTracking()
            .Where(l => l.UserId == userId && l.Purpose == SetupLinkPurpose.PasswordReset).ToListAsync());

    private static Task SelfResetAsync(AppFactory factory, string email) =>
        WithScope(factory, async sp =>
        {
            await sp.GetRequiredService<AccountService>().RequestPasswordResetAsync(email);
            return 0;
        });

    [Fact]
    public async Task Self_reset_inside_the_cooldown_after_an_admin_reset_keeps_the_admin_link()
    {
        var (factory, clock, user, adminId) = await ResetSetupAsync(mailCooldownMinutes: 0);
        await using var _ = factory;
        var adminToken = await AdminResetAsync(factory, user, adminId);

        clock.Now += TimeSpan.FromMinutes(5);
        await SelfResetAsync(factory, "user@example.com");

        Assert.True(await LinkLiveAsync(factory, adminToken));
        Assert.Single(await ResetLinksAsync(factory, user.Id));
    }

    [Fact]
    public async Task Self_reset_whose_mail_is_dropped_keeps_the_admin_link()
    {
        // Outbox per-type cooldown (60 min) is longer than the reset-link cooldown (15 min).
        var (factory, clock, user, adminId) = await ResetSetupAsync(mailCooldownMinutes: 60);
        await using var _ = factory;
        var adminToken = await AdminResetAsync(factory, user, adminId);
        Assert.Equal(1, await factory.SendMailAsync());

        clock.Now += TimeSpan.FromMinutes(20);
        await SelfResetAsync(factory, "user@example.com");

        Assert.Equal(0, await factory.SendMailAsync()); // dropped
        Assert.True(await LinkLiveAsync(factory, adminToken));
        Assert.Single(await ResetLinksAsync(factory, user.Id));
    }

    [Fact]
    public async Task Self_reset_after_the_cooldown_with_mail_queued_replaces_the_admin_link()
    {
        var (factory, clock, user, adminId) = await ResetSetupAsync(mailCooldownMinutes: 0);
        await using var _ = factory;
        var adminToken = await AdminResetAsync(factory, user, adminId);

        clock.Now += TimeSpan.FromMinutes(20);
        await SelfResetAsync(factory, "user@example.com");

        Assert.Equal(2, await factory.SendMailAsync());
        Assert.False(await LinkLiveAsync(factory, adminToken));
        var links = await ResetLinksAsync(factory, user.Id);
        Assert.Equal(2, links.Count);
        Assert.Single(links, l => l.UsedAt is null && l.CreatedBy is null);
    }

    // ---- 5. shared browser --------------------------------------------------------------------------

    [Fact]
    public async Task Logout_and_login_rotate_the_voter_cookie()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var visitorId = await CreateUserAsync(factory, "visitor@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var (browser, jar) = factory.CreateBrowserWithJar();

        // Anonymous vote, then someone logs in on the same browser: the ballot is not theirs.
        await GetPageAsync(browser, $"/p/{id}");
        Assert.True((await SubmitRankedAsync(factory, id, VisitorFrom(factory, jar, id), ParisFirst)).Succeeded);
        Assert.Contains("You have already voted", await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(browser, "visitor@example.com", Password)).StatusCode);
        Assert.Null(VisitorFrom(factory, jar, id).LinkVoterKey);
        Assert.DoesNotContain("You have already voted", await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());

        // Signed-in vote (recorded by account since Phase 5b), then logout: the next person does not see it.
        Assert.True((await SubmitRankedAsync(factory, id, VisitorFrom(factory, jar, id, visitorId), ParisFirst)).Succeeded);
        Assert.Contains("You have already voted", await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());
        var logout = await AppFactory.SubmitFormAsync(browser, "/Account/Logout", "logout",
            new Dictionary<string, string> { ["ReturnUrl"] = "" }, tokenSourcePath: "/Account/Manage");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Null(VisitorFrom(factory, jar, id).LinkVoterKey);
        Assert.DoesNotContain("You have already voted", await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());
        var ballots = await BallotsAsync(factory, id);
        Assert.Equal(2, ballots.Count);
        Assert.Single(ballots, b => b.UserId == visitorId && b.VoterKey is null);
    }

    // ---- 30. foreign polls are a real 404; 20. links use App.PublicUrl; 9. forgot password -------

    [Fact]
    public async Task Edit_and_share_of_someone_elses_poll_are_404()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "other@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var other = await LoggedInBrowserAsync(factory, "other@example.com");
        var missing = PollId.Encode(Guid.NewGuid());

        foreach (var path in new[] { $"/polls/{id}/edit", $"/polls/{id}/share", $"/polls/{missing}/edit", $"/polls/{missing}/share" })
        {
            var response = await other.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} -> {response.StatusCode}");
            Assert.DoesNotContain("Trip", await response.Content.ReadAsStringAsync());
        }

        var mine = await LoggedInBrowserAsync(factory, "o@example.com");
        Assert.Equal(HttpStatusCode.OK, (await mine.GetAsync($"/polls/{id}/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mine.GetAsync($"/polls/{id}/share")).StatusCode);
    }

    [Fact]
    public async Task Share_link_uses_the_public_url_when_set()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        Assert.Contains($"value=\"https://localhost/p/{id}\"", await (await browser.GetAsync($"/polls/{id}/share")).Content.ReadAsStringAsync());

        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.PublicUrl] = "https://vote.example.org/" }, null);
        var html = await (await browser.GetAsync($"/polls/{id}/share")).Content.ReadAsStringAsync();
        Assert.Contains($"value=\"https://vote.example.org/p/{id}\"", html);
        Assert.DoesNotContain("https://localhost/p/", html);
    }

    [Fact]
    public async Task Forgot_password_without_mail_says_recovery_is_unavailable()
    {
        await using var factory = new AppFactory();
        var html = await (await factory.CreateBrowser().GetAsync("/Account/ForgotPassword")).Content.ReadAsStringAsync();
        Assert.Contains("Password recovery is not available on this server", html);
        Assert.DoesNotContain("Send link", html);

        await ConfigureMailAsync(factory);
        html = await (await factory.CreateBrowser().GetAsync("/Account/ForgotPassword")).Content.ReadAsStringAsync();
        Assert.Contains("Send link", html);
    }
}
