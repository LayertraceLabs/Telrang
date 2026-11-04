using System.Net;
using System.Security.Claims;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Status;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Phase 5b part 2: per-user limits (§14.4), abuse handling and captcha (§14.7), the Status tab
/// (§14.8), and the remaining user-flow review findings (recovery key, 2FA rules, mail honesty, revoking
/// used invites, dead mail, pending links, lockout and error pages), plus flows that had no test.
/// </summary>
public class Phase5b2Tests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], []);

    private static PollDraft Draft(string title = "Trip", VoterMode voters = VoterMode.Public) =>
        RankedDraft(ResultsVisibility.AfterVote, voters, title).Describe("Where?");

    private static Task<PollSaveResult> CreateRawAsync(AppFactory factory, string ownerId, PollDraft source) =>
        WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(ownerId, source));

    private static Task<SubmitResult> SubmitAsync(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot,
        int? responseNo = null, BallotGuard? guard = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, null, responseNo, guard));

    private static Task SetAsync(AppFactory factory, Dictionary<string, string?> values) => factory.Settings.SetAsync(values, null);

    /// <summary>A fresh vote form stamp, as the vote page renders it.</summary>
    private static string Stamp(AppFactory factory) => factory.Services.GetRequiredService<FormGuard>().CreateStamp();

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
        WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id));

    /// <summary>Turns 2FA on for an account and returns the authenticator key (base32).</summary>
    private static Task<string> EnableTwoFactorAsync(AppFactory factory, string userId) => WithScope(factory, async sp =>
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId))!;
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
        return (await users.GetAuthenticatorKeyAsync(user))!;
    });

    private static HttpClient Peer(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Add(PeerHeader, ip);
        return client;
    }

    private sealed class FakeCaptcha : ICaptchaVerifier
    {
        public int Calls;

        public Task<CaptchaResult> VerifyAsync(CaptchaProvider provider, string secret, string token, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(secret == "sekret" && token == "good" ? CaptchaResult.Accepted : CaptchaResult.Rejected);
        }
    }

    // ---- limits (§14.4) ---------------------------------------------------------------------------

    [Fact]
    public async Task Open_polls_limit_refuses_at_the_boundary_and_counts_reopening()
    {
        await using var factory = new AppFactory();
        await SetAsync(factory, new() { [SettingKeys.LimitActivePolls] = "2" });
        var owner = await CreateUserAsync(factory, "o@example.com");
        var first = await CreatePollAsync(factory, owner, Draft("One"));
        await CreatePollAsync(factory, owner, Draft("Two"));

        var refused = await CreateRawAsync(factory, owner, Draft("Three"));
        Assert.Equal(PollSaveStatus.LimitReached, refused.Status);
        Assert.Equal("Limit reached: 2 open polls. Close one first.", refused.Message);

        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, first, true)));
        Assert.True((await CreateRawAsync(factory, owner, Draft("Three"))).Succeeded);

        // Reopening would make three open polls.
        var reopen = await WithScope(factory, sp => sp.GetRequiredService<PollService>().ChangeClosedAsync(owner, first, false));
        Assert.Equal((false, "Limit reached: 2 open polls. Close one first."), reopen);
    }

    [Fact]
    public async Task New_polls_per_day_is_a_rolling_24_hours()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await SetAsync(factory, new() { [SettingKeys.LimitPollsPerDay] = "2", [SettingKeys.LimitActivePolls] = "0" });
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreatePollAsync(factory, owner, Draft("One"));
        clock.Now += TimeSpan.FromHours(12);
        await CreatePollAsync(factory, owner, Draft("Two"));

        var refused = await CreateRawAsync(factory, owner, Draft("Three"));
        Assert.Equal("Limit reached: 2 new polls per 24 hours.", refused.Message);

        clock.Now += TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1); // the first one left the window
        Assert.True((await CreateRawAsync(factory, owner, Draft("Three"))).Succeeded);
    }

    [Fact]
    public async Task Per_user_override_beats_the_default_and_admins_set_it()
    {
        await using var factory = new AppFactory();
        await SetAsync(factory, new() { [SettingKeys.LimitActivePolls] = "1" });
        var (_, admin) = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreatePollAsync(factory, owner, Draft("One"));
        Assert.Equal(PollSaveStatus.LimitReached, (await CreateRawAsync(factory, owner, Draft("Two"))).Status);

        var saved = await Admin(factory, a => a.SetUserLimitsAsync(admin, owner, new UserLimitOverrides(3, null, null, null, null, null)));
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal(3, (await UserAsync(factory, owner)).LimitActivePolls);
        Assert.True((await CreateRawAsync(factory, owner, Draft("Two"))).Succeeded);
        Assert.True((await CreateRawAsync(factory, owner, Draft("Three"))).Succeeded);
        Assert.Equal("Limit reached: 3 open polls. Close one first.", (await CreateRawAsync(factory, owner, Draft("Four"))).Message);

        var row = (await Admin(factory, a => a.ListUsersAsync(admin)))!.Single(u => u.Id == owner);
        Assert.Equal(3, row.Limits.ActivePolls);
        Assert.False((await Admin(factory, a => a.SetUserLimitsAsync(admin, owner, new UserLimitOverrides(-1, null, null, null, null, null)))).Ok);
    }

    [Fact]
    public async Task Self_registered_accounts_get_half_the_creation_limits_for_24_hours()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await SetAsync(factory, new() { [SettingKeys.LimitPollsPerDay] = "4", [SettingKeys.LimitActivePolls] = "0" });
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.RegisterAsync(factory.CreateBrowser(), "new@example.com", Password)).StatusCode);
        var userId = await ConfirmEmailAsync(factory, "new@example.com");
        Assert.True((await UserAsync(factory, userId)).SelfRegistered);

        await CreatePollAsync(factory, userId, Draft("One"));
        await CreatePollAsync(factory, userId, Draft("Two"));
        Assert.Equal("Limit reached: 2 new polls per 24 hours.", (await CreateRawAsync(factory, userId, Draft("Three"))).Message);

        // Admin-made and older accounts get the full limit.
        var regular = await CreateUserAsync(factory, "old@example.com");
        for (var i = 0; i < 4; i++) await CreatePollAsync(factory, regular, Draft($"P{i}"));
        clock.Now += TimeSpan.FromHours(25);
        Assert.True((await CreateRawAsync(factory, userId, Draft("Three"))).Succeeded);
    }

    [Fact]
    public async Task Ballots_per_poll_and_responses_per_voter_are_capped_but_revisions_pass()
    {
        await using var factory = new AppFactory();
        await SetAsync(factory, new() { [SettingKeys.LimitBallotsPerPoll] = "2", [SettingKeys.LimitResponsesPerVoter] = "2" });
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var a = VisitorContext.ForUser(await CreateUserAsync(factory, "a@example.com"));
        var b = VisitorContext.ForUser(await CreateUserAsync(factory, "b@example.com"));
        var c = VisitorContext.ForUser(await CreateUserAsync(factory, "c@example.com"));
        Assert.True((await SubmitAsync(factory, id, a, ParisFirst)).Succeeded);
        Assert.True((await SubmitAsync(factory, id, b, ParisFirst)).Succeeded);
        var full = await SubmitAsync(factory, id, c, ParisFirst);
        Assert.Equal(SubmitStatus.Denied, full.Status);
        Assert.Equal(["Limit reached: 2 ballots per poll."], full.Errors);
        Assert.True((await SubmitAsync(factory, id, a, new RankedBallot(["rome"], []))).Succeeded); // revising is not a new ballot

        var many = await CreatePollAsync(factory, owner, Draft("Family").Many());
        Assert.True((await SubmitAsync(factory, many, a, ParisFirst)).Succeeded);
        Assert.True((await SubmitAsync(factory, many, a, ParisFirst)).Succeeded);
        Assert.Equal(["Limit reached: 2 responses per voter."], (await SubmitAsync(factory, many, a, ParisFirst)).Errors);
        Assert.True((await SubmitAsync(factory, many, a, ParisFirst, responseNo: 2)).Succeeded);

        // An owner override lifts it for that owner's polls.
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var u = (await users.FindByIdAsync(owner))!;
            u.LimitBallotsPerPoll = 0; // no limit
            return await users.UpdateAsync(u);
        });
        Assert.True((await SubmitAsync(factory, id, c, ParisFirst)).Succeeded);
    }

    [Fact]
    public async Task Invite_links_per_poll_count_existing_tokens_and_revoked_ones_free_room()
    {
        await using var factory = new AppFactory();
        await SetAsync(factory, new() { [SettingKeys.LimitInvitesPerPoll] = "3" });
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        PollService Polls(IServiceProvider sp) => sp.GetRequiredService<PollService>();

        var (made, error) = await WithScope(factory, sp => Polls(sp).GenerateInvitesCheckedAsync(owner, id, 2, []));
        Assert.Null(error);
        Assert.Equal(2, made!.Count);
        var (none, refused) = await WithScope(factory, sp => Polls(sp).GenerateInvitesCheckedAsync(owner, id, 2, []));
        Assert.Null(none);
        Assert.Equal("Limit reached: 3 invite links per poll (1 left).", refused);

        var list = (await WithScope(factory, sp => Polls(sp).ListInvitesAsync(owner, id)))!;
        Assert.True(await WithScope(factory, sp => Polls(sp).RevokeInviteAsync(owner, id, list[0].Id)));
        Assert.Null((await WithScope(factory, sp => Polls(sp).GenerateInvitesCheckedAsync(owner, id, 2, []))).Error);
        Assert.Equal(3, (await WithScope(factory, sp => Polls(sp).ListInvitesAsync(owner, id)))!.Count);
    }

    [Fact]
    public async Task Max_duration_limits_closes_and_auto_closes_polls_without_it()
    {
        var clock = new MutableTime(new DateTimeOffset(2030, 3, 1, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await SetAsync(factory, new() { [SettingKeys.LimitPollDurationDays] = "30" });
        var owner = await CreateUserAsync(factory, "o@example.com");

        var tooLate = await CreateRawAsync(factory, owner, Draft().Closes("2030-04-01"));
        Assert.Equal(PollSaveStatus.Invalid, tooLate.Status);
        var error = Assert.Single(tooLate.Errors);
        Assert.Equal("closesOn", error.Field);
        Assert.Equal("Pick a date on or before 2030-03-31 (at most 30 days after creation).", error.Message);
        Assert.True((await CreateRawAsync(factory, owner, Draft().Closes("2030-03-31"))).Succeeded);

        // No closes: the computed close is stored and shown, and "closed" is the same everywhere.
        var id = await CreatePollAsync(factory, owner, Draft("Open-ended"));
        Assert.True(PollId.TryDecode(id, out var pollId));
        var poll = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking().SingleAsync(p => p.Id == pollId));
        Assert.Equal(clock.Now + TimeSpan.FromDays(30), poll.ClosesAt);
        var page = await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("title=\"Maximum poll duration\">Closes 2030-03-31", page);
        Assert.True(poll.AutoClose);

        var voter = VisitorContext.ForUser(await CreateUserAsync(factory, "v@example.com"));
        Assert.True((await SubmitAsync(factory, id, voter, ParisFirst)).Succeeded);
        clock.Now += TimeSpan.FromDays(30);
        var late = await SubmitAsync(factory, id, voter, ParisFirst);
        Assert.Equal(["Poll closed."], late.Errors);
        Assert.Contains((await WithScope(factory, sp => sp.GetRequiredService<PollService>().ListOwnedAsync(owner))), p => p.EncodedId == id && p.IsClosed);
    }

    // ---- bot checks (§14.7) -----------------------------------------------------------------------

    [Fact]
    public async Task Honeypot_refuses_the_form_records_a_signal_and_blocks_the_client()
    {
        await using var factory = new AppFactory(services: AddFakePeer);
        var browser = Peer(factory.CreateBrowser(), "198.51.100.20");
        var response = await AppFactory.SubmitFormAsync(browser, "/Account/Register", "register", new Dictionary<string, string>
        {
            ["Input.Email"] = "bot@example.com",
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
            ["website"] = "https://spam.example",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Form expired", await response.Content.ReadAsStringAsync());
        Assert.Null(await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("bot@example.com")));

        // A honeypot hit weighs 10 of the default 20: a second one blocks the address.
        await AppFactory.SubmitFormAsync(browser, "/Account/Register", "register",
            new Dictionary<string, string> { ["Input.Email"] = "bot2@example.com", ["website"] = "x" });
        var blocked = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Contains("Try again in 1 minute.", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "198.51.100.21").GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Signed_form_stamp_must_be_two_seconds_to_two_hours_old()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Abuse:FormMinSeconds"] = "2" },
            services: s => s.AddSingleton<TimeProvider>(clock));
        var browser = factory.CreateBrowser();
        var html = await (await browser.GetAsync("/Account/Register")).Content.ReadAsStringAsync();
        Dictionary<string, string> Form(string email) => new()
        {
            ["__RequestVerificationToken"] = AppFactory.ScrapeAntiforgeryToken(html),
            ["_handler"] = "register",
            ["_fts"] = AppFactory.ScrapeFormStamp(html)!,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
        };

        var fast = await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(Form("fast@example.com")));
        Assert.Equal(HttpStatusCode.BadRequest, fast.StatusCode);
        Assert.Contains("Submitted too fast. Wait a few seconds and submit again.", await fast.Content.ReadAsStringAsync());

        var missing = Form("nostamp@example.com");
        missing.Remove("_fts");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(missing))).StatusCode);

        clock.Now += TimeSpan.FromHours(3);
        var old = await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(Form("old@example.com")));
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        Assert.Contains("Reload the page", await old.Content.ReadAsStringAsync());

        html = await (await browser.GetAsync("/Account/Register")).Content.ReadAsStringAsync();
        clock.Now += TimeSpan.FromSeconds(3);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(Form("human@example.com")))).StatusCode);

        // The vote form carries the same stamp; BallotService applies the same minimum (DESIGN.md §21.1).
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var visitor = new VisitorContext(null, "voter-key-1", null);
        var guard = new BallotGuard(Stamp: Stamp(factory));
        Assert.Equal(["Submitted too fast. Wait a few seconds and submit again."], (await SubmitAsync(factory, id, visitor, ParisFirst, guard: guard)).Errors);
        clock.Now += TimeSpan.FromSeconds(3);
        Assert.True((await SubmitAsync(factory, id, visitor, ParisFirst, guard: guard)).Succeeded);
    }

    [Fact]
    public async Task Vote_with_a_filled_honeypot_is_refused()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var visitor = new VisitorContext(null, "voter-key-1", null);
        var refused = await SubmitAsync(factory, id, visitor, ParisFirst, guard: new BallotGuard(Honeypot: "me@example.com", Stamp: Stamp(factory)));
        Assert.Equal(SubmitStatus.Denied, refused.Status);
        Assert.Empty(await BallotsAsync(factory, id));
        Assert.True((await SubmitAsync(factory, id, visitor, ParisFirst, guard: new BallotGuard(Stamp: Stamp(factory)))).Succeeded);
    }

    [Fact]
    public async Task Abuse_blocks_escalate_1_10_60_minutes_and_reset_after_a_quiet_day()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        var tracker = new AbuseTracker(clock, Options.Create(new AbuseOptions { Threshold = 3 }));
        var ip = IPAddress.Parse("203.0.113.5");
        void Hit(int n = 3)
        {
            for (var i = 0; i < n; i++) tracker.Record("203.0.113.5", null, AbuseSignal.FailedLogin);
        }

        Hit(2);
        Assert.Null(tracker.Find(ip, null));
        clock.Now += TimeSpan.FromMinutes(11); // outside the 10 min window
        Hit(1);
        Assert.Null(tracker.Find(ip, null));

        var expected = new[] { 1, 10, 60, 60 };
        foreach (var minutes in expected)
        {
            Hit();
            var block = tracker.Find(ip, null);
            Assert.NotNull(block);
            Assert.Equal(clock.Now + TimeSpan.FromMinutes(minutes), block.Until);
            Assert.Equal(nameof(AbuseSignal.FailedLogin), block.Reason);
            clock.Now += TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(1);
            Assert.Null(tracker.Find(ip, null));
        }

        clock.Now += TimeSpan.FromHours(25);
        Hit();
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(1), tracker.Find(ip, null)!.Until);

        // IPv6 clients are tracked per /48.
        for (var i = 0; i < 3; i++) tracker.Record(ClientKeys.ForAbuse(IPAddress.Parse($"2001:db8:1:{i}::1")), null, AbuseSignal.Honeypot);
        Assert.NotNull(tracker.Find(IPAddress.Parse("2001:db8:1:ffff::9"), null));
        Assert.Null(tracker.Find(IPAddress.Parse("2001:db8:2::1"), null));
    }

    [Fact]
    public async Task Failed_logins_block_an_address_admins_pass_and_can_clear_or_add_blocks()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Abuse:Threshold"] = "3" }, services: AddFakePeer);
        var (_, admin) = await AdminAsync(factory);
        var userId = await CreateUserAsync(factory, "user@example.com");
        const string Ip = "198.51.100.7";
        var adminBrowser = Peer(factory.CreateBrowser(), Ip);
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(adminBrowser, "admin@example.com", Password)).StatusCode);

        var attacker = Peer(factory.CreateBrowser(), Ip);
        for (var i = 0; i < 3; i++) await AppFactory.LoginAsync(attacker, "user@example.com", "wrong password!!");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await attacker.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminBrowser.GetAsync("/admin?tab=abuse")).StatusCode); // admins are never blocked

        var blocks = (await Admin(factory, a => a.ListBlocksAsync(admin)))!;
        var block = Assert.Single(blocks);
        Assert.Equal((AbuseKind.Ip, Ip, "FailedLogin"), (block.Kind, block.Key, block.Reason));
        Assert.True((await Admin(factory, a => a.ClearBlockAsync(admin, block.Id))).Ok);
        Assert.Equal(HttpStatusCode.OK, (await attacker.GetAsync("/")).StatusCode);

        // Manual blocks are for accounts only; networks go through Blocked ranges (one mechanism, with its checks).
        Assert.False((await Admin(factory, a => a.AddBlockAsync(admin, "not-an-ip", 5, null))).Ok);
        var network = await Admin(factory, a => a.AddBlockAsync(admin, "198.51.100.0/24", 5, "spam wave"));
        Assert.Equal((false, "Enter an account's email. Block addresses under Blocked ranges."), (network.Ok, network.Message));
        Assert.Equal(HttpStatusCode.OK, (await Peer(factory.CreateBrowser(), "198.51.100.200").GetAsync("/")).StatusCode);

        var user = Peer(factory.CreateBrowser(), "192.0.2.2");
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(user, "user@example.com", Password)).StatusCode);
        Assert.True((await Admin(factory, a => a.AddBlockAsync(admin, "user@example.com", 5, null))).Ok);
        // The block ends the account's sessions (server-side sessions, pentest R3-DEP-2); signed in again, it is refused.
        Assert.Equal(HttpStatusCode.Redirect, (await user.GetAsync("/polls")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(user, "user@example.com", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await user.GetAsync("/polls")).StatusCode);
        var listed = (await Admin(factory, a => a.ListBlocksAsync(admin)))!;
        Assert.Contains(listed, b => b.Kind == AbuseKind.Account && b.Label == "user@example.com" && b.Key == userId);
        Assert.True((await Admin(factory, a => a.ClearBlockAsync(admin, listed.Single(b => b.Kind == AbuseKind.Account).Id))).Ok);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/polls")).StatusCode);
    }

    // ---- captcha ------------------------------------------------------------------------------------

    [Fact]
    public async Task Captcha_applies_only_to_anonymous_public_votes_and_is_verified()
    {
        var fake = new FakeCaptcha();
        await using var factory = new AppFactory(services: s => s.AddSingleton<ICaptchaVerifier>(fake));
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());

        // Off: no CSP origins, no widget.
        var csp = (await factory.CreateBrowser().GetAsync("/")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.DoesNotContain("challenges.cloudflare.com", csp);

        await SetAsync(factory, new()
        {
            [SettingKeys.CaptchaProvider] = "Turnstile",
            [SettingKeys.CaptchaSiteKey] = "site-key",
            [SettingKeys.CaptchaSecret] = "sekret",
        });
        Assert.True(factory.Settings.Current.Captcha.Enabled);
        Assert.NotEqual("sekret", factory.Settings.Current.Raw[SettingKeys.CaptchaSecret]); // protected at rest
        csp = (await factory.CreateBrowser().GetAsync("/")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' 'nonce-", csp);
        Assert.Contains("https://challenges.cloudflare.com", csp);
        Assert.Contains("frame-src 'self' https://challenges.cloudflare.com", csp);

        var page = await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("challenges.cloudflare.com/turnstile/v0/api.js", page);
        Assert.Matches("<script src=\"https://challenges\\.cloudflare\\.com[^\"]*\" nonce=\"[0-9A-F]+\"", page);

        var anonymous = new VisitorContext(null, "voter-key-1", null);
        Assert.Equal([BallotService.CaptchaMissingMessage], (await SubmitAsync(factory, id, anonymous, ParisFirst)).Errors);
        Assert.Equal([BallotService.CaptchaFailedMessage], (await SubmitAsync(factory, id, anonymous, ParisFirst, guard: new BallotGuard(CaptchaToken: "bad", Stamp: Stamp(factory)))).Errors);
        Assert.True((await SubmitAsync(factory, id, anonymous, ParisFirst, guard: new BallotGuard(CaptchaToken: "good", Stamp: Stamp(factory)))).Succeeded);

        // Signed-in voters and invite polls never see it.
        var calls = fake.Calls;
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(owner), ParisFirst)).Succeeded);
        var invitePoll = await CreatePollAsync(factory, owner, Draft("Invite", voters: VoterMode.Invite));
        var invite = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, invitePoll, 1, [])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        Assert.True((await SubmitAsync(factory, invitePoll, new VisitorContext(null, null, hash), ParisFirst)).Succeeded);
        Assert.Equal(calls, fake.Calls);
        Assert.DoesNotContain("turnstile", await (await GetPageAsync(await LoggedInBrowserAsync(factory, "o@example.com"), $"/p/{id}")).Content.ReadAsStringAsync());
    }

    // ---- status tab (§14.8) -------------------------------------------------------------------------

    [Fact]
    public async Task Status_and_abuse_tabs_render_with_counters_and_the_warning_log()
    {
        await using var factory = new AppFactory();
        await AdminAsync(factory);
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        await AppFactory.LoginAsync(factory.CreateBrowser(), "admin@example.com", "wrong password!!");
        factory.Services.GetRequiredService<ILogger<Phase5b2Tests>>().LogWarning("Disk {State}", "almost full");

        var rates = factory.Services.GetRequiredService<RateCounters>();
        Assert.True(rates.Sum("av.failed_logins", TimeSpan.FromHours(1)) >= 1);
        Assert.True(rates.Sum("av.requests", TimeSpan.FromHours(24)) >= 3);
        var log = factory.Services.GetRequiredService<LogRing>().Snapshot();
        Assert.Contains(log, e => e.Message == "Disk almost full" && e.Level == LogLevel.Warning);
        Assert.DoesNotContain(log, e => e.Level < LogLevel.Warning);

        var status = await browser.GetAsync("/admin?tab=status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var html = await status.Content.ReadAsStringAsync();
        Assert.Contains("Failed sign-ins", html);
        Assert.Contains("Disk almost full", html);
        Assert.Contains("Public URL is empty", html); // finding 21
        Assert.Contains("Mail is not configured and registration is open", html); // finding 8
        Assert.Contains(AppVersion.Display, html);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin?tab=abuse")).StatusCode);
    }

    // ---- recovery key (finding 5) ---------------------------------------------------------------------

    [Fact]
    public async Task Recovery_key_resets_an_admins_password_and_2fa_once()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["App:IssueRecoveryKey"] = "true" });
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync("/setup/recover")).StatusCode); // no admin at startup: no key
        var (adminId, _) = await AdminAsync(factory);
        await EnableTwoFactorAsync(factory, adminId);
        var key = factory.Services.GetRequiredService<RecoveryKey>().Issue(); // what startup logs when an admin exists

        var browser = factory.CreateBrowser();
        var wrong = await AppFactory.SubmitFormAsync(browser, "/setup/recover", "recover-key", new Dictionary<string, string> { ["KeyInput.Key"] = "nope" });
        var wrongHtml = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("Recovery key is wrong", wrongHtml);
        Assert.DoesNotContain("admin@example.com", wrongHtml); // the admin list needs the key

        var step = await AppFactory.SubmitFormAsync(browser, "/setup/recover", "recover-key", new Dictionary<string, string> { ["KeyInput.Key"] = key });
        var html = await step.Content.ReadAsStringAsync();
        Assert.Contains("admin@example.com", html);
        var reset = await browser.PostAsync("/setup/recover", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.ScrapeAntiforgeryToken(html),
            ["_handler"] = "recover-reset",
            ["_fts"] = AppFactory.ScrapeFormStamp(html)!,
            ["ResetInput.Key"] = key,
            ["ResetInput.AdminId"] = adminId,
            ["ResetInput.Password"] = "a brand new admin passphrase",
            ["ResetInput.ConfirmPassword"] = "a brand new admin passphrase",
        }));
        Assert.Contains("Admin recovered", await reset.Content.ReadAsStringAsync());

        var admin = await UserAsync(factory, adminId);
        Assert.False(admin.TwoFactorEnabled);
        Assert.True(admin.MustEnrolTwoFactor); // admins must keep 2FA: enrol again at sign-in
        var login = await AppFactory.LoginAsync(factory.CreateBrowser(), "admin@example.com", "a brand new admin passphrase");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.DoesNotContain("LoginWith2fa", login.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync("/setup/recover")).StatusCode); // single use
        Assert.True(RecoveryKey.Requested(factory.Services.GetRequiredService<IConfiguration>()));
    }

    // ---- 2FA rules (findings 28, 29) -------------------------------------------------------------------

    [Fact]
    public async Task Turning_2fa_off_needs_a_code_and_admins_cannot()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "me@example.com");
        var key = await EnableTwoFactorAsync(factory, userId);
        var browser = await LoggedInWithTotpAsync(factory, "me@example.com");
        Assert.DoesNotContain("Admins must keep 2FA on.", await (await browser.GetAsync("/Account/Manage/TwoFactorAuthentication")).Content.ReadAsStringAsync());

        var wrong = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/Disable2fa", "disable-2fa", new Dictionary<string, string> { ["Code"] = "000000" });
        Assert.Contains("Code is wrong", await wrong.Content.ReadAsStringAsync());
        Assert.True((await UserAsync(factory, userId)).TwoFactorEnabled);
        var ok = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/Disable2fa", "disable-2fa", new Dictionary<string, string> { ["Code"] = Phase5aTests.Totp(key) });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.False((await UserAsync(factory, userId)).TwoFactorEnabled);

        var (adminId, _) = await AdminAsync(factory);
        var adminKey = await EnableTwoFactorAsync(factory, adminId);
        var adminBrowser = await LoggedInWithTotpAsync(factory, "admin@example.com");
        Assert.DoesNotContain("Account/Manage/Disable2fa", await (await adminBrowser.GetAsync("/Account/Manage/TwoFactorAuthentication")).Content.ReadAsStringAsync());
        var refused = await adminBrowser.GetAsync("/Account/Manage/Disable2fa");
        Assert.Contains("Admins must keep 2FA on.", await refused.Content.ReadAsStringAsync());
        Assert.True((await UserAsync(factory, adminId)).TwoFactorEnabled);
        _ = adminKey;
    }

    // ---- mail honesty, dead mail, pending links (findings 8, 13, 14, 24) ----------------------------------

    [Fact]
    public async Task Register_page_says_when_mail_is_off()
    {
        await using var factory = new AppFactory();
        Assert.Contains("No verification mail here", await (await factory.CreateBrowser().GetAsync("/Account/Register")).Content.ReadAsStringAsync());
        await ConfigureMailAsync(factory);
        Assert.DoesNotContain("No verification mail here", await (await factory.CreateBrowser().GetAsync("/Account/Register")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dead_mail_can_be_purged()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory);
        var (_, admin) = await AdminAsync(factory);
        async Task AddDeadAsync(string to) => await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), To = to, Subject = "s", TextBody = "t", Type = MailType.Test, CreatedAt = DateTimeOffset.UtcNow,
                SendAfter = DateTimeOffset.UtcNow, Attempts = 5, LastError = "boom", DeadAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });
        await AddDeadAsync("b@example.test");
        Assert.Equal("1 dead message deleted.", (await Admin(factory, a => a.PurgeDeadMailAsync(admin))).Message);
        Assert.Equal(0, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.CountAsync()));
    }

    [Fact]
    public async Task Pending_accounts_show_link_expiry_and_issuer_and_expired_links_say_so()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var (_, admin) = await AdminAsync(factory);
        var created = await Admin(factory, a => a.CreateAccountAsync(admin, "https://localhost/"));
        var path = new Uri(created.Link!).PathAndQuery;
        var pending = (await Admin(factory, a => a.ListUsersAsync(admin)))!.Single(u => u.PendingSetup);
        Assert.Equal("admin@example.com", pending.PendingLinkBy);
        Assert.InRange(pending.PendingLinkExpiresAt!.Value, clock.Now + TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1), clock.Now + TimeSpan.FromDays(7));
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateBrowser().GetAsync(path)).StatusCode);

        // Expired after 7 days: a 404 that says what to do (finding 27); a renewed link works.
        clock.Now += TimeSpan.FromDays(8);
        var expired = await factory.CreateBrowser().GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Contains("Ask an admin for a new one", await expired.Content.ReadAsStringAsync());
        var renewed = await Admin(factory, a => a.RenewSetupLinkAsync(admin, pending.Id, "https://localhost/"));
        Assert.True(renewed.Ok);
        var fresh = await factory.CreateBrowser().GetAsync(new Uri(renewed.Link!).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Contains("Set up account", await fresh.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync(path)).StatusCode); // old one superseded
    }

    [Fact]
    public async Task Setup_page_says_how_to_get_a_new_key_when_it_expired()
    {
        await using var factory = new AppFactory();
        factory.SetupKey.Revoke();
        var response = await factory.CreateBrowser().GetAsync("/setup");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Restart the server for a new key", await response.Content.ReadAsStringAsync());
    }

    // ---- invites (finding 12, untested flow) -----------------------------------------------------------

    [Fact]
    public async Task Used_invite_can_be_revoked_and_its_ballot_stays()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var invite = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, ["Ann"])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        var visitor = new VisitorContext(null, null, hash);
        Assert.True((await SubmitAsync(factory, id, visitor, ParisFirst)).Succeeded);
        var token = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().ListInvitesAsync(owner, id)))!.Single();
        Assert.True(token.Used);

        Assert.False(await WithScope(factory, sp => sp.GetRequiredService<PollService>().RevokeInviteAsync(owner, id, token.Id)));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().RevokeInviteAsync(owner, id, token.Id, includeUsed: true)));
        Assert.Single(await BallotsAsync(factory, id));
        var after = await SubmitAsync(factory, id, visitor, new RankedBallot(["rome"], []));
        Assert.Equal(["This invite link is not valid for this poll."], after.Errors);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync($"/p/{id}?t={invite.Token}")).StatusCode);
    }

    // ---- sign-in flows (findings 22, 23; untested flows) -----------------------------------------------

    [Fact]
    public async Task Admin_lock_and_failed_attempts_show_different_lockout_lines()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var locked = await CreateUserAsync(factory, "locked@example.com");
        await CreateUserAsync(factory, "guess@example.com");
        Assert.True((await Admin(factory, a => a.LockAsync(admin, locked))).Ok);

        var browser = factory.CreateBrowser();
        var login = await AppFactory.LoginAsync(browser, "locked@example.com", Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("Account/Lockout?by=admin", login.Headers.Location!.ToString());
        Assert.Contains("An admin locked this account", await (await browser.GetAsync(login.Headers.Location)).Content.ReadAsStringAsync());

        for (var i = 0; i < 5; i++) await AppFactory.LoginAsync(browser, "guess@example.com", "wrong password!!");
        var lockedOut = await AppFactory.LoginAsync(browser, "guess@example.com", Password);
        Assert.EndsWith("Account/Lockout", lockedOut.Headers.Location!.ToString());
        Assert.Contains("Too many failed sign-ins. Try again in 15 minutes", await (await browser.GetAsync(lockedOut.Headers.Location)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recovery_code_signs_in_once()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "me@example.com");
        await EnableTwoFactorAsync(factory, userId);
        var codes = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(userId))!, 10))!.ToList();
        });

        async Task<HttpResponseMessage> SignInWithCodeAsync(HttpClient browser)
        {
            var login = await AppFactory.LoginAsync(browser, "me@example.com", Password);
            Assert.Contains("LoginWith2fa", login.Headers.Location!.ToString());
            return await AppFactory.SubmitFormAsync(browser, "/Account/LoginWithRecoveryCode", "login-with-recovery-code",
                new Dictionary<string, string> { ["Input.RecoveryCode"] = codes[0] });
        }

        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await SignInWithCodeAsync(browser)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/polls")).StatusCode);
        Assert.Equal(9, await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.CountRecoveryCodesAsync((await users.FindByIdAsync(userId))!);
        }));

        var reused = await SignInWithCodeAsync(factory.CreateBrowser());
        Assert.Contains("Invalid recovery code", await reused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Duplicate_registration_answers_like_a_new_one()
    {
        await using var factory = new AppFactory();
        var first = await AppFactory.RegisterAsync(factory.CreateBrowser(), "dup@example.com", Password);
        var again = await AppFactory.RegisterAsync(factory.CreateBrowser(), "dup@example.com", Password);
        Assert.Equal(first.StatusCode, again.StatusCode);
        Assert.Equal(first.Headers.Location, again.Headers.Location);
        Assert.DoesNotContain("is already taken", await again.Content.ReadAsStringAsync());
    }

    // ---- error pages (findings 25, 32) -----------------------------------------------------------------

    [Fact]
    public async Task Rate_limit_antiforgery_and_404_pages_are_short_html()
    {
        await using var factory = new AppFactory();
        var form = await factory.CreateBrowser().PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Email"] = "x@example.com" }));
        Assert.Equal(HttpStatusCode.BadRequest, form.StatusCode);
        Assert.Contains("<h1>Form expired</h1>", await form.Content.ReadAsStringAsync());

        var browser = factory.CreateBrowser();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 11; i++) last = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("text/html", last.Content.Headers.ContentType!.MediaType);
        var tooMany = await last.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Too many requests</h1>", tooMany);
        Assert.DoesNotContain("<script", tooMany);


        var missing = await factory.CreateBrowser().GetAsync("/p/AAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var notFound = await missing.Content.ReadAsStringAsync();
        Assert.Contains("Check the address", notFound);
        Assert.DoesNotContain("Sorry", notFound);
    }
}
