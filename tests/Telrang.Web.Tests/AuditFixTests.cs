using System.Net;
using System.Security.Claims;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Fixes from the 2026-10-26 audit (DESIGN.md §13 "audit fixes"): the BUG, DEAD END, SIMPLIFY and UNTESTED items.
/// </summary>
public class AuditFixTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], []);
    private static readonly RankedBallot RomeFirst = new(["rome", "paris"], []);

    private static PollDraft Draft(VoterMode voters = VoterMode.Public, ResultsVisibility results = ResultsVisibility.AfterVote, string title = "Trip") =>
        RankedDraft(results, voters, title).Describe("Where?");

    private static Task<SubmitResult> SubmitAsync(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot, string? name = null,
        bool notify = false) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, name, null, null, notify));

    private static Task<PollAccess?> AccessAsync(AppFactory factory, string id, VisitorContext visitor) =>
        WithScope(factory, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id, visitor));

    private static Task<T> Polls<T>(AppFactory factory, Func<PollService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<PollService>()));

    private static Task<T> Admin<T>(AppFactory factory, Func<AdminService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<AdminService>()));

    private static Task<PollSaveResult> EditAsync(AppFactory factory, string owner, string id, PollDraft source) =>
        Polls(factory, p => p.UpdatePollAsync(owner, id, source));

    private static async Task<Poll> PollRowAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking().SingleAsync(p => p.Id == pollId));
    }

    private static Task SetAsync(AppFactory factory, string key, string? value) =>
        factory.Settings.SetAsync(new Dictionary<string, string?> { [key] = value }, null);

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

    private static async Task<string> CreateUnverifiedAsync(AppFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = false };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return user.Id;
    }

    private static async Task UpdateUserAsync(AppFactory factory, string userId, Action<ApplicationUser> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId))!;
        change(user);
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    private static async Task<(HttpClient Client, CookieContainer Jar)> SignedInBrowserAsync(AppFactory factory, string email, HttpClient? client = null,
        CookieContainer? jar = null)
    {
        if (client is null) (client, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(client, email, Password)).StatusCode);
        return (client, jar!);
    }

    private static bool HasCookie(CookieContainer jar, string id, string name) =>
        jar.GetCookies(new Uri($"https://localhost/p/{id}"))[name] is not null;

    private static async Task<string> PageAsync(HttpClient client, string path) =>
        await (await GetPageAsync(client, path)).Content.ReadAsStringAsync();

    private static async Task<(string Token, string Hash)> InviteAsync(AppFactory factory, string owner, string id)
    {
        var invite = (await Polls(factory, p => p.GenerateInvitesAsync(owner, id, 1, [])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        return (invite.Token, hash);
    }

    private static async Task ConfigureMailAndUrlAsync(AppFactory factory)
    {
        await ConfigureMailAsync(factory);
        await SetAsync(factory, SettingKeys.PublicUrl, "https://vote.example.test");
    }

    private static async Task<List<OutboxMessage>> ResultsMailsAsync(AppFactory factory) =>
        Revealed(factory, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.Type == MailType.ResultsReady).ToListAsync()));

    private static async Task SetNotifyAsync(AppFactory factory, string id, string userId)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Ballots.Where(b => b.PollId == pollId && b.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.NotifyOnClose, true)));
    }

    // ---- BUG 1: invite cookie on a shared browser --------------------------------------------------------

    [Fact]
    public async Task Shared_browser_the_next_account_cannot_see_or_take_over_an_anonymous_invite_ballot()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var y = await CreateUserAsync(factory, "y@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite).Names(NamesMode.Optional));
        var (token, hash) = await InviteAsync(factory, owner, id);

        // X, anonymous, opens the link and votes with a name.
        var (browser, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync($"/p/{id}?t={token}")).StatusCode);
        Assert.True((await SubmitAsync(factory, id, VisitorFrom(factory, jar, id), ParisFirst, "Xavier")).Succeeded);

        // Y signs in on the same browser. The cookie was issued to "anonymous": it does not count for Y.
        await SignedInBrowserAsync(factory, "y@example.com", browser, jar);
        var visitorY = VisitorFrom(factory, jar, id, y);
        Assert.Null(visitorY.InviteTokenHash);
        var page = await PageAsync(browser, $"/p/{id}");
        Assert.DoesNotContain("Xavier", page);
        Assert.DoesNotContain("You have already voted", page);
        Assert.DoesNotContain("ballot-editor", page);
        Assert.Contains("invite-only", page);
        Assert.False(HasCookie(jar, id, PollCookies.InviteCookieName(id))); // dropped for the next person

        // Y's submit (the takeover in the audit) is refused; X's ballot stays the token's.
        var refused = await SubmitAsync(factory, id, visitorY, RomeFirst);
        Assert.False(refused.Succeeded);
        Assert.Equal(AccessDenial.InviteRequired, refused.Access!.Denial);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal("k:invite:" + hash, ballot.VoterIdentity);
        Assert.Null(ballot.UserId);
        Assert.Equal("Xavier", ballot.DisplayName);
        Assert.Empty(await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(y)));

        // A cookie issued to an account is not read for anyone else either (signed out on that browser).
        var (other, otherJar) = await SignedInBrowserAsync(factory, "y@example.com");
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync($"/p/{id}?t={token}")).StatusCode);
        Assert.Equal(hash, VisitorFrom(factory, otherJar, id, y).InviteTokenHash);
        Assert.Null(VisitorFrom(factory, otherJar, id).InviteTokenHash);
        Assert.Null(VisitorFrom(factory, otherJar, id, owner).InviteTokenHash);
    }

    // ---- BUG 2 + SIMPLIFY 21: ClosesAt frozen at creation ------------------------------------------------

    [Fact]
    public async Task Text_only_edits_never_move_the_close_even_after_the_admin_changes_the_limit()
    {
        var clock = new MutableTime(new DateTimeOffset(2030, 3, 1, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "o@example.com");
        var created = clock.Now;
        var id = await CreatePollAsync(factory, owner, Draft(title: "Frozen"));
        var poll = await PollRowAsync(factory, id);
        Assert.Equal(created + TimeSpan.FromDays(180), poll.ClosesAt);
        Assert.True(poll.AutoClose);
        Assert.Contains("\"closesOn\":null", poll.DefinitionJson); // what the AuditFixes migration's backfill matches

        // The admin lowers the limit to 30 days; on day 60 the owner fixes a typo: the poll stays open.
        await SetAsync(factory, SettingKeys.LimitPollDurationDays, "30");
        clock.Now += TimeSpan.FromDays(60);
        Assert.True((await EditAsync(factory, owner, id, Draft(title: "Frozen poll"))).Succeeded);
        Assert.Equal(created + TimeSpan.FromDays(180), (await PollRowAsync(factory, id)).ClosesAt);
        Assert.False((await AccessAsync(factory, id, VisitorContext.ForUser(owner)))!.IsClosed);

        // Limit 0 (no auto-close) and another text edit: still the same close, not open-ended.
        await SetAsync(factory, SettingKeys.LimitPollDurationDays, "0");
        Assert.True((await EditAsync(factory, owner, id, Draft(title: "Frozen poll 2"))).Succeeded);
        poll = await PollRowAsync(factory, id);
        Assert.Equal(created + TimeSpan.FromDays(180), poll.ClosesAt);
        Assert.True(poll.AutoClose);

        // A typed closes: date is a change of the close date: recomputed (limit 0, so no duration check).
        Assert.True((await EditAsync(factory, owner, id, Draft(title: "Frozen poll 2").Closes("2031-06-30"))).Succeeded);
        poll = await PollRowAsync(factory, id);
        Assert.Equal(Stored(PollTime.CloseInstant(new DateOnly(2031, 6, 30), PollTime.Utc)), poll.ClosesAt);
        Assert.False(poll.AutoClose);
        Assert.Contains("\"closesOn\":\"2031-06-30\"", poll.DefinitionJson);

        // The time zone is fixed at creation (DESIGN.md §26): refused, the close stays.
        Assert.False((await EditAsync(factory, owner, id, Draft(title: "Frozen poll 2").Closes("2031-06-30").Zone("Europe/Berlin"))).Succeeded);
        Assert.Equal(Stored(PollTime.CloseInstant(new DateOnly(2031, 6, 30), PollTime.Utc)), (await PollRowAsync(factory, id)).ClosesAt);

        // Removing closes: changes the close date too: creation + the owner's limit now (0 = none).
        Assert.True((await EditAsync(factory, owner, id, Draft(title: "Frozen poll 2"))).Succeeded);
        poll = await PollRowAsync(factory, id);
        Assert.Null(poll.ClosesAt);
        Assert.False(poll.AutoClose);

        // From now on text edits keep "no close", even with a limit again.
        await SetAsync(factory, SettingKeys.LimitPollDurationDays, "30");
        Assert.True((await EditAsync(factory, owner, id, Draft(title: "Frozen poll 3"))).Succeeded);
        Assert.Null((await PollRowAsync(factory, id)).ClosesAt);

        // The share page says which kind of close it is.
        var id2 = await CreatePollAsync(factory, owner, Draft(title: "Auto"));
        Assert.True((await PollRowAsync(factory, id2)).AutoClose);
        var page = await PageAsync(factory.CreateBrowser(), $"/p/{id2}");
        Assert.Contains("title=\"Maximum poll duration\">Closes " + PollTime.Format(clock.Now + TimeSpan.FromDays(30), PollTime.Utc), page);
    }

    [Fact]
    public async Task Migration_marks_existing_automatic_closes()
    {
        await using var factory = new AppFactory(); // for the Identity options the model is built with
        var path = Path.Combine(factory.DataDir, "upgrade.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path}")
            .UseApplicationServiceProvider(factory.Services)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261026101154_TodoSweep");
            await InsertLegacyUserAsync(db, "owner", "o@example.com"); // the current model has later columns
            foreach (var (title, closesAt, json) in new (string, string?, string)[]
                     {
                         ("Auto", "2026-06-01 00:00:00+00:00", """{"title":"Auto","closesOn":null,"timeZone":"UTC"}"""),
                         ("Typed", "2026-03-01 00:00:00+00:00", """{"title":"Typed","closesOn":"2026-02-28","timeZone":"UTC"}"""),
                         ("Legacy", "2026-03-01 00:00:00+00:00", """{"title":"Legacy","closesAt":"2026-02-28T23:59:59+00:00"}"""),
                         ("Endless", null, """{"title":"Endless","closesOn":null}"""),
                     })
            {
                var id = Guid.NewGuid().ToString().ToUpperInvariant();
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"Polls\" (\"Id\", \"OwnerId\", \"Title\", \"Type\", \"Scoring\", \"Source\", \"DefinitionJson\", \"PasswordVersion\", \"ResultsVisibility\", \"VoterMode\", \"ClosesAt\", \"CreatedAt\", \"UpdatedAt\") VALUES ({id}, 'owner', {title}, 'Ranked', 'Borda', '', {json}, 0, 'Creator', 'Public', {closesAt}, '2026-01-01 08:00:00+00:00', '2026-01-01 08:00:00+00:00')");
            }
        }
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.MigrateAsync();
            var auto = await db.Polls.AsNoTracking().ToDictionaryAsync(p => p.Title, p => p.AutoClose);
            Assert.True(auto["Auto"]);
            Assert.False(auto["Typed"]);
            Assert.False(auto["Legacy"]);
            Assert.False(auto["Endless"]);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // ---- BUG 3: editor preview ---------------------------------------------------------------------------

    [Fact]
    public async Task Editor_preview_shows_the_automatic_close_instead_of_when_you_close_it()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var (browser, _) = await SignedInBrowserAsync(factory, "o@example.com");

        var create = await PageAsync(browser, "/polls/new"); // a new poll has no close date
        Assert.Contains("automatically after 180 days", create);
        Assert.DoesNotContain("until you close it", create);

        var id = await CreatePollAsync(factory, owner, Draft());
        var poll = await PollRowAsync(factory, id);
        var edit = await PageAsync(browser, $"/polls/{id}/edit");
        Assert.Contains("automatically on " + PollTime.Format(poll.ClosesAt!.Value, PollTime.Utc), edit);
        Assert.DoesNotContain("until you close it", edit);

        // Only with no limit at all does the preview say the owner closes it.
        await SetAsync(factory, SettingKeys.LimitPollDurationDays, "0");
        Assert.Contains("until you close it", await PageAsync(browser, "/polls/new"));
    }

    // ---- BUG 4: drafts per account -----------------------------------------------------------------------

    [Fact]
    public async Task Draft_key_names_the_account_so_another_account_never_gets_the_draft()
    {
        await using var factory = new AppFactory();
        var admin = await CreateUserAsync(factory, "admin@example.com");
        var other = await CreateUserAsync(factory, "new@example.com");
        var (adminBrowser, _) = await SignedInBrowserAsync(factory, "admin@example.com");
        var (otherBrowser, _) = await SignedInBrowserAsync(factory, "new@example.com");

        var adminNew = await PageAsync(adminBrowser, "/polls/new");
        var otherNew = await PageAsync(otherBrowser, "/polls/new");
        Assert.Contains($"data-draft-key=\"draft:{admin}:new\"", adminNew);
        Assert.Contains($"data-draft-key=\"draft:{other}:new\"", otherNew);
        Assert.DoesNotContain(admin, otherNew);

        var id = await CreatePollAsync(factory, admin, Draft());
        Assert.Contains($"data-draft-key=\"draft:{admin}:{id}\"", await PageAsync(adminBrowser, $"/polls/{id}/edit"));
        Assert.Null(PollEditor.DraftKeyFor(null, id));
        Assert.Equal($"draft:{other}:new", PollEditor.DraftKeyFor(other, null));
    }

    // ---- BUG 5: voter name length setting ----------------------------------------------------------------

    [Fact]
    public async Task Voter_name_length_setting_applies_to_the_service_and_the_input()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Names(NamesMode.Optional));
        Assert.Equal(60, BallotService.NameLimit(factory.Settings.Current.Limits));

        foreach (var bad in new[] { "5", "121" })
        {
            var refused = await Admin(factory, a => a.SaveSettingsAsync(admin, new Dictionary<string, string?> { [SettingKeys.LimitVoterName] = bad }, "settings.save"));
            Assert.Equal((false, "Voter name length: 10 to 120."), (refused.Ok, refused.Message));
        }
        Assert.True((await Admin(factory, a => a.SaveSettingsAsync(admin, new Dictionary<string, string?> { [SettingKeys.LimitVoterName] = "10" }, "settings.save"))).Ok);

        var tooLong = await SubmitAsync(factory, id, VisitorContext.ForUser(owner), ParisFirst, new string('x', 40));
        Assert.Equal(["Shorten your name to 10 characters."], tooLong.Errors);
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(owner), ParisFirst, "Ten chars!")).Succeeded);
        Assert.Contains("maxlength=\"10\"", await PageAsync(factory.CreateBrowser(), $"/p/{id}"));

        // A value written around the admin form is clamped.
        await SetAsync(factory, SettingKeys.LimitVoterName, "500");
        Assert.Equal(120, BallotService.NameLimit(factory.Settings.Current.Limits));
    }

    // ---- BUG 6: unsubscribe needs a POST -----------------------------------------------------------------

    [Fact]
    public async Task Unsubscribe_get_only_shows_the_button_and_post_switches_off_idempotently()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAndUrlAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var alice = await CreateUserAsync(factory, "alice@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(alice), ParisFirst, notify: true)).Succeeded);
        Assert.True(PollId.TryDecode(id, out var pollId));
        var link = "/notify/off/" + factory.Services.GetRequiredService<PollSweepService>().UnsubscribeToken(alice, pollId);
        var browser = factory.CreateBrowser();

        // A scanner's GET (twice) changes nothing.
        for (var i = 0; i < 2; i++)
        {
            var get = await browser.GetAsync(link);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var html = await get.Content.ReadAsStringAsync();
            Assert.Contains("Stop emails", html);
            Assert.DoesNotContain("You will not be emailed", html);
        }
        Assert.True(Assert.Single(await BallotsAsync(factory, id)).NotifyOnClose);

        // The button's POST does it, and again harmlessly.
        for (var i = 0; i < 2; i++)
        {
            var post = await AppFactory.SubmitFormAsync(browser, link, "notify-off", new Dictionary<string, string>());
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            Assert.Contains("You will not be emailed about this poll.", await post.Content.ReadAsStringAsync());
        }
        Assert.False(Assert.Single(await BallotsAsync(factory, id)).NotifyOnClose);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(link + "x")).StatusCode);
    }

    // ---- BUG 7: results-ready mail re-checks the recipient's access ----------------------------------------

    [Fact]
    public async Task Results_mail_goes_only_to_recipients_who_can_still_see_the_results()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAndUrlAsync(factory);
        var (_, admin) = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var alice = await CreateUserAsync(factory, "alice@example.com");
        var bob = await CreateUserAsync(factory, "bob@example.com");
        var carol = await CreateUserAsync(factory, "carol@example.com");
        await CreateUserAsync(factory, "dave@example.com");

        // Normal recipient.
        var normal = await CreatePollAsync(factory, owner, Draft(title: "Normal"));
        Assert.True((await SubmitAsync(factory, normal, VisitorContext.ForUser(alice), ParisFirst, notify: true)).Succeeded);

        // Revoked invitation: the revoke clears the flag; even a flag left on is not mailed (no access).
        var invite = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite, title: "Invite"));
        var (_, hash) = await InviteAsync(factory, owner, invite);
        Assert.True((await SubmitAsync(factory, invite, new VisitorContext(bob, null, hash), ParisFirst, notify: true)).Succeeded);
        Assert.True(Assert.Single(await BallotsAsync(factory, invite)).NotifyOnClose);
        var token = Assert.Single((await Polls(factory, p => p.ListInvitesAsync(owner, invite)))!);
        Assert.True(await Polls(factory, p => p.RevokeInviteAsync(owner, invite, token.Id, includeUsed: true)));
        Assert.False(Assert.Single(await BallotsAsync(factory, invite)).NotifyOnClose);
        await SetNotifyAsync(factory, invite, bob);

        // Transferred owner-only poll: the old owner voted in it; the transfer clears the flag, and he no longer sees
        // the results, so a flag left on is not mailed either.
        var secret = await CreatePollAsync(factory, carol, Draft(title: "Secret", results: ResultsVisibility.Creator));
        Assert.True((await SubmitAsync(factory, secret, VisitorContext.ForUser(carol), ParisFirst, notify: true)).Succeeded);
        Assert.True((await Admin(factory, a => a.TransferPollAsync(admin, secret, "dave@example.com"))).Ok);
        Assert.False(Assert.Single(await BallotsAsync(factory, secret)).NotifyOnClose);
        await SetNotifyAsync(factory, secret, carol);
        Assert.False((await AccessAsync(factory, secret, VisitorContext.ForUser(carol)))!.CanSeeResults);

        foreach (var id in new[] { normal, invite, secret })
            await Admin(factory, a => a.SetPollClosedAsync(admin, id, true));
        Assert.Equal(1, await factory.Services.GetRequiredService<PollSweepService>().NotifyClosedAsync());
        var mail = Assert.Single(await ResultsMailsAsync(factory));
        Assert.Equal("alice@example.com", mail.To);
        Assert.Contains("Normal", mail.Subject);
    }

    // ---- BUG 8: admin stylesheet variant on pages that must always work --------------------------------------

    [Fact]
    public async Task Login_setup_notify_and_error_pages_link_the_theme_without_custom_css()
    {
        await using var factory = new AppFactory(services: AddFakePeer);
        await SetAsync(factory, SettingKeys.ThemeCustomCss, "body { display: none; }");
        var theme = factory.Services.GetRequiredService<ThemeService>();
        var adminHref = $"href=\"{WebUtility.HtmlEncode(theme.AdminUrl)}\"";
        var publicHref = $"href=\"{theme.Url}\"";
        var client = factory.CreateBrowser();

        foreach (var path in new[] { "/Account/Login", "/Account/Register", "/Account/ForgotPassword", "/setup", "/notify/off/garbage", "/not-found" })
        {
            var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.Contains(adminHref, html);
            Assert.DoesNotContain(publicHref, html);
        }
        Assert.Contains(publicHref, await (await client.GetAsync("/")).Content.ReadAsStringAsync());

        // The static blocked page too.
        Assert.Null((await factory.Services.GetRequiredService<BlockedRangeService>().AddAsync("198.51.100.0/24", null, null, null)).Error);
        var blocked = new HttpRequestMessage(HttpMethod.Get, "/");
        blocked.Headers.Add(PeerHeader, "198.51.100.7");
        var page = await client.SendAsync(blocked);
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("theme.css?admin=1", await page.Content.ReadAsStringAsync());
    }

    // ---- BUG 9: theme swap and the share page clock -----------------------------------------------------

    [Fact]
    public async Task Theme_swap_replaces_the_href_of_one_link()
    {
        await using var factory = new AppFactory();
        var client = factory.CreateClient();
        Assert.Contains("id=\"theme-css\" data-theme-css", await client.GetStringAsync("/"));
        var js = await client.GetStringAsync("/js/theme.js");
        Assert.Contains("link[data-theme-css]", js);
        Assert.Contains("setAttribute('href', href)", js);
        Assert.DoesNotContain("createElement", js); // no second sheet, so no race
    }

    [Fact]
    public async Task Share_page_closing_date_note_follows_the_injected_clock()
    {
        var clock = new MutableTime(new DateTimeOffset(2030, 1, 28, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Closes("2030-01-31"));
        var (browser, _) = await SignedInBrowserAsync(factory, "o@example.com");
        Assert.DoesNotContain("A later close date reopens it", await PageAsync(browser, $"/polls/{id}/share"));
        clock.Now = new DateTimeOffset(2030, 2, 2, 12, 0, 0, TimeSpan.Zero); // the wall clock (2026) has not passed it; within the sign-in lifetime
        Assert.Contains("title=\"A later close date reopens it\">Closed ", await PageAsync(browser, $"/polls/{id}/share"));
    }

    // ---- DEAD END 11: revoked claimed invite on My votes --------------------------------------------------

    [Fact]
    public async Task My_votes_lists_a_revoked_invitation_without_links()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite, results: ResultsVisibility.AfterVote));
        var (_, hash) = await InviteAsync(factory, owner, id);
        Assert.True((await SubmitAsync(factory, id, new VisitorContext(me, null, hash), ParisFirst)).Succeeded);
        var token = Assert.Single((await Polls(factory, p => p.ListInvitesAsync(owner, id)))!);
        Assert.True(await Polls(factory, p => p.RevokeInviteAsync(owner, id, token.Id, includeUsed: true)));

        var row = Assert.Single(await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(me)));
        Assert.True(row.Revoked);
        Assert.False(row.ResultsLink);
        var (browser, _) = await SignedInBrowserAsync(factory, "me@example.com");
        var html = await PageAsync(browser, "/my/votes");
        Assert.Contains("Invitation revoked", html);
        Assert.DoesNotContain($"href=\"p/{id}", html);
    }

    // ---- DEAD END 12: transferred poll beyond the new owner's limit ----------------------------------------

    [Fact]
    public async Task New_owner_can_edit_a_transferred_poll_whose_close_is_beyond_his_limit()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var target = await CreateUserAsync(factory, "t@example.com");
        await UpdateUserAsync(factory, owner, u => u.LimitPollDurationDays = 400);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        PollDraft Text(DateOnly closes, string title = "Long") => Draft(title: title).Closes(closes);
        var id = await CreatePollAsync(factory, owner, Text(today.AddDays(300)));
        Assert.True((await Admin(factory, a => a.TransferPollAsync(admin, id, "t@example.com"))).Ok);

        // Unchanged close date: saves.
        Assert.True((await EditAsync(factory, target, id, Text(today.AddDays(300), "Long poll"))).Succeeded);
        // A changed date is checked against the new owner's limit (180 days).
        var moved = await EditAsync(factory, target, id, Text(today.AddDays(299), "Long poll"));
        Assert.Equal(PollSaveStatus.Invalid, moved.Status);
        var error = Assert.Single(moved.Errors);
        Assert.Equal("closesOn", error.Field);
        Assert.Contains("at most 180 days after creation", error.Message);
        Assert.True((await EditAsync(factory, target, id, Text(today.AddDays(100), "Long poll"))).Succeeded);
    }

    // ---- DEAD END 14 + SIMPLIFY 15: one block mechanism for addresses ---------------------------------------

    [Fact]
    public async Task Address_blocks_go_through_blocked_ranges_which_refuse_everything_loopback_and_proxies()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["ReverseProxy:KnownNetworks"] = "10.1.0.0/16" });
        var (_, admin) = await AdminAsync(factory);

        foreach (var (cidr, message) in new[]
                 {
                     ("0.0.0.0/0", "That range covers every address."),
                     ("::/0", "That range covers every address."),
                     ("127.0.0.1", "That range includes loopback."),
                     ("10.1.2.0/24", "That range includes a trusted reverse proxy."), // configured trusted networks
                 })
        {
            var refused = await Admin(factory, a => a.AddRangeAsync(admin, cidr, null, 1));
            Assert.Equal((false, message), (refused.Ok, refused.Message));
        }
        var network = await Admin(factory, a => a.AddBlockAsync(admin, "0.0.0.0/0", 60, null));
        Assert.False(network.Ok);
        Assert.Empty((await Admin(factory, a => a.ListBlocksAsync(admin)))!);
        Assert.True((await Admin(factory, a => a.AddRangeAsync(admin, "203.0.113.0/24", "temporary", 1))).Ok);

        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var tab = await (await browser.GetAsync("/admin?tab=abuse")).Content.ReadAsStringAsync();
        Assert.Contains("Account email", tab);
        Assert.DoesNotContain("IP, CIDR or email", tab);
        Assert.Contains("203.0.113.0/24", tab);
    }

    // ---- SIMPLIFY: one closed rule, one account check, one size limit, CanSeeResults on My votes ---------------

    [Fact]
    public void PollState_is_the_one_closed_rule()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(PollState.IsClosed(null, null, now));
        Assert.False(PollState.IsClosed(null, now.AddSeconds(1), now));
        Assert.True(PollState.IsClosed(null, now, now));
        Assert.True(PollState.IsClosed(now.AddDays(-1), now.AddDays(5), now));
        Assert.Equal(now.AddDays(-1), PollState.ClosedOn(now.AddDays(-1), now.AddDays(5)));
        Assert.Equal(now.AddDays(2), PollState.ClosedOn(null, now.AddDays(2)));
        Assert.Null(PollState.AutoCloseAt(now, 0));
        Assert.Equal(now.AddDays(180), PollState.AutoCloseAt(now, 180));
    }

    [Fact]
    public async Task Poll_description_limit_defaults_to_2000_and_follows_the_setting()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        Assert.Equal(2000, factory.Settings.Current.Limits.PollDescriptionLength);
        Assert.Equal(2000, PollParsing.LimitsFrom(factory.Settings.Current.Limits).DescriptionMax);
        Assert.True((await Polls(factory, p => p.CreatePollAsync(owner, Draft().Describe(new string('x', 2000))))).Succeeded);
        var refused = await Polls(factory, p => p.CreatePollAsync(owner, Draft().Describe(new string('x', 2001))));
        Assert.Equal(PollSaveStatus.Invalid, refused.Status);
        Assert.Equal(new DraftError("description", "Shorten the poll description to 2000 characters (now 2001)."), Assert.Single(refused.Errors));

        // The admin's setting overrides the default, in the service and in the limits the editor uses.
        await SetAsync(factory, SettingKeys.LimitPollDescription, "3000");
        Assert.Equal(3000, await Polls(factory, p => Task.FromResult(p.CurrentLimits.DescriptionMax)));
        Assert.True((await Polls(factory, p => p.CreatePollAsync(owner, Draft().Describe(new string('x', 2500))))).Succeeded);
        await SetAsync(factory, SettingKeys.LimitPollDescription, "10");
        Assert.Equal("description", Assert.Single((await Polls(factory, p => p.CreatePollAsync(owner, Draft().Describe("eleven char")))).Errors).Field);
    }

    [Fact]
    public async Task One_account_check_refuses_gated_accounts_everywhere()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAndUrlAsync(factory);
        var (_, admin) = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var gated = await CreateUserAsync(factory, "gated@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Accounts));
        await UpdateUserAsync(factory, gated, u => u.MustEnrolTwoFactor = true);

        await using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await AccountGate.MayActAsync(db, gated));
        Assert.True(await AccountGate.MayActAsync(db, owner, verifiedEmail: true));
        Assert.Equal(AccessDenial.EmailNotVerified, (await AccessAsync(factory, id, VisitorContext.ForUser(gated)))!.Denial);
        Assert.False((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, VisitorContext.ForUser(gated))))!.CanNotify);
        Assert.False(await Polls(factory, p => p.HasVerifiedEmailAsync(gated)));
        Assert.False((await Admin(factory, a => a.TransferPollAsync(admin, id, "gated@example.com"))).Ok);
    }

    [Fact]
    public async Task My_votes_results_link_is_the_access_check()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var later = await CreatePollAsync(factory, owner, Draft(title: "Later").With(d => d.ResultsTiming = ResultsTiming.AfterClose));
        var hidden = await CreatePollAsync(factory, owner, Draft(title: "Hidden", results: ResultsVisibility.Creator));
        foreach (var poll in new[] { id, later, hidden })
            Assert.True((await SubmitAsync(factory, poll, VisitorContext.ForUser(me), ParisFirst)).Succeeded);
        async Task<bool> Link(string title) =>
            (await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(me))).Single(v => v.Title == title).ResultsLink;
        Assert.True(await Link("Trip"));
        Assert.False(await Link("Hidden")); // owner-only: no dead link

        // Results after closing (DESIGN.md §25): the link appears once the poll closes.
        Assert.False(await Link("Later"));
        Assert.True(await Polls(factory, p => p.SetClosedAsync(owner, later, true)));
        Assert.True(await Link("Later"));
    }

    // ---- UNTESTED ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Poll_deleted_mid_vote_says_it_no_longer_exists()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Many());
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(me), ParisFirst)).Succeeded);
        Assert.True(await Polls(factory, p => p.DeletePollAsync(owner, id)));

        var submit = await SubmitAsync(factory, id, VisitorContext.ForUser(me), RomeFirst);
        Assert.Equal(SubmitStatus.NotFound, submit.Status);
        Assert.Equal(["This poll no longer exists."], submit.Errors);
        var delete = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().DeleteResponseAsync(id, VisitorContext.ForUser(me), 1));
        Assert.Equal(["This poll no longer exists."], delete.Errors);
    }

    /// <summary>
    /// DESIGN.md §14.3a: only <c>voters: accounts</c> requires a verified email; an invite link is a capability, so an
    /// unverified account votes with it and (§16) claims it like any account.
    /// </summary>
    [Fact]
    public async Task Unverified_account_votes_with_an_invite_link_and_claims_it()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var unverified = await CreateUnverifiedAsync(factory, "u@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var (_, hash) = await InviteAsync(factory, owner, id);

        Assert.True((await SubmitAsync(factory, id, new VisitorContext(unverified, null, hash), ParisFirst)).Succeeded);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(unverified, ballot.UserId);
        var tokens = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().InviteTokens.AsNoTracking().ToListAsync());
        Assert.Equal(unverified, Assert.Single(tokens).ClaimedByUserId);
    }

    [Fact]
    public async Task Transfer_keeps_claims_and_voter_subscriptions_and_moves_listings_only()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAndUrlAsync(factory);
        var (_, admin) = await AdminAsync(factory);
        var oldOwner = await CreateUserAsync(factory, "old@example.com");
        var newOwner = await CreateUserAsync(factory, "new@example.com");
        var voter = await CreateUserAsync(factory, "voter@example.com");

        // The voter subscribed; the old owner voted and subscribed too.
        var secret = await CreatePollAsync(factory, oldOwner, Draft(title: "Secret"));
        Assert.True((await SubmitAsync(factory, secret, VisitorContext.ForUser(voter), ParisFirst, notify: true)).Succeeded);
        Assert.True((await SubmitAsync(factory, secret, VisitorContext.ForUser(oldOwner), RomeFirst, notify: true)).Succeeded);
        // Invite poll: the voter claimed a token.
        var invite = await CreatePollAsync(factory, oldOwner, Draft(voters: VoterMode.Invite, title: "Invite"));
        var (_, hash) = await InviteAsync(factory, oldOwner, invite);
        Assert.True((await SubmitAsync(factory, invite, new VisitorContext(voter, null, hash), ParisFirst)).Succeeded);

        foreach (var id in new[] { secret, invite })
            Assert.True((await Admin(factory, a => a.TransferPollAsync(admin, id, "new@example.com"))).Ok);

        // Claims untouched.
        Assert.True((await AccessAsync(factory, secret, VisitorContext.ForUser(voter)))!.CanSeeResults);
        var token = Assert.Single(await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().InviteTokens.AsNoTracking().ToListAsync()));
        Assert.Equal(voter, token.ClaimedByUserId);
        Assert.Equal(voter, Assert.Single(await BallotsAsync(factory, invite)).UserId);

        // Notify flags: the voter's stays; the old owner's is cleared.
        var ballots = await BallotsAsync(factory, secret);
        Assert.True(ballots.Single(b => b.UserId == voter).NotifyOnClose);
        Assert.False(ballots.Single(b => b.UserId == oldOwner).NotifyOnClose);

        // Listings: the polls moved; the old owner keeps his own vote on My votes (after-vote: with the results link).
        var newPolls = await Polls(factory, p => p.ListOwnedAsync(newOwner));
        Assert.Equal(2, newPolls.Count);
        Assert.Empty(await Polls(factory, p => p.ListOwnedAsync(oldOwner)));
        var oldVotes = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(oldOwner));
        Assert.True(Assert.Single(oldVotes).ResultsLink);
        Assert.False((await AccessAsync(factory, secret, VisitorContext.ForUser(oldOwner)))!.CanEdit);

        // The new owner edits with his own draft key; the old owner's browser draft (his key) is never offered to him.
        var (newBrowser, _) = await SignedInBrowserAsync(factory, "new@example.com");
        var edit = await PageAsync(newBrowser, $"/polls/{secret}/edit");
        Assert.Contains($"data-draft-key=\"draft:{newOwner}:{secret}\"", edit);
        Assert.DoesNotContain(oldOwner, edit);
        Assert.True((await EditAsync(factory, newOwner, secret, Draft(title: "Secret poll"))).Succeeded);
    }
}
