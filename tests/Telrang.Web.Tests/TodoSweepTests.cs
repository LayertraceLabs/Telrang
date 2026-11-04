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
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// The TODO sweep (DESIGN.md §13, 2026-10-26): auto-close backfill, poll creations
/// per day, favicon, admin theme without custom CSS, mail retry steps, notify-me mail, ownership transfer and
/// retention.
/// </summary>
public class TodoSweepTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], []);
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static PollDraft Draft(string title = "Trip", ResultsVisibility results = ResultsVisibility.AfterVote) =>
        RankedDraft(results, VoterMode.Public, title).Describe("Where?");

    private static Task<SubmitResult> SubmitAsync(AppFactory factory, string id, VisitorContext visitor, bool notify, int? responseNo = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ParisFirst, null, responseNo, null, notify));

    private static Task<T> Admin<T>(AppFactory factory, Func<AdminService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<AdminService>()));

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

    private static DateTimeOffset WholeSecondNow() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static Task<List<Ballot>> BallotsOfAsync(AppFactory factory, string id) => BallotsAsync(factory, id);

    private static async Task<List<OutboxMessage>> OutboxAsync(AppFactory factory, MailType type) =>
        Revealed(factory, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking().Where(m => m.Type == type).ToListAsync()));

    private static async Task ConfigureMailAndUrlAsync(AppFactory factory)
    {
        await ConfigureMailAsync(factory);
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.PublicUrl] = "https://vote.example.test" }, null);
    }

    // ---- 2. auto-close backfill ---------------------------------------------------------------------

    [Fact]
    public async Task Migration_backfills_auto_close_from_the_owner_or_global_duration()
    {
        await using var factory = new AppFactory(); // for the Identity options the model is built with
        var path = Path.Combine(factory.DataDir, "upgrade.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path}")
            .UseApplicationServiceProvider(factory.Services)
            // The model is the current one; this test is about the migrations' SQL, not the snapshot diff.
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261026090852_Phase6Branding");
            // Raw rows: the current model has columns this schema does not have yet.
            await InsertLegacyUserAsync(db, "plain", "plain@example.com");
            await InsertLegacyUserAsync(db, "unlimited", "u@example.com", new Dictionary<string, object?> { ["LimitPollDurationDays"] = 0 });
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO \"AppSettings\" (\"Key\", \"Value\", \"UpdatedAt\") VALUES ('Limits.MaxPollDurationDays', '30', '2026-01-01 00:00:00+00:00')");
            foreach (var (title, owner) in new[] { ("Old", "plain"), ("Dated", "plain"), ("Free", "unlimited") })
            {
                var pollId = Guid.NewGuid().ToString().ToUpperInvariant();
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"Polls\" (\"Id\", \"OwnerId\", \"Title\", \"Type\", \"Scoring\", \"Source\", \"DefinitionJson\", \"PasswordVersion\", \"ResultsVisibility\", \"VoterMode\", \"CreatedAt\", \"UpdatedAt\") VALUES ({pollId}, {owner}, {title}, 'Ranked', 'Borda', '', '', 0, 'Creator', 'Public', '2026-01-01 08:00:00.1234567+00:00', '2026-01-01 08:00:00+00:00')");
            }
            await db.Database.ExecuteSqlAsync($"UPDATE \"Polls\" SET \"ClosesAt\" = '2026-03-01 00:00:00+00:00' WHERE \"Title\" = 'Dated'");
        }
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.MigrateAsync();
            var closes = await db.Polls.AsNoTracking().ToDictionaryAsync(p => p.Title, p => p.ClosesAt);
            Assert.Equal(new DateTimeOffset(2026, 1, 31, 8, 0, 0, 123, TimeSpan.Zero), closes["Old"]); // created + 30 days
            Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), closes["Dated"]); // untouched
            Assert.Null(closes["Free"]); // the owner's override 0 = no limit
        }
        SqliteConnection.ClearAllPools();
    }

    // ---- 3. poll creations per day --------------------------------------------------------------------

    [Fact]
    public async Task Polls_per_day_counts_creations_so_delete_and_recreate_counts_twice()
    {
        var clock = new MutableTime(WholeSecondNow()); // stored times keep milliseconds only
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitPollsPerDay] = "2" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var first = await CreatePollAsync(factory, owner, Draft("One"));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().DeletePollAsync(owner, first)));
        await CreatePollAsync(factory, owner, Draft("One again"));

        var refused = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(owner, Draft("Three")));
        Assert.Equal(PollSaveStatus.LimitReached, refused.Status);
        Assert.Equal("Limit reached: 2 new polls per 24 hours.", refused.Message);

        // Rows older than 48 h are purged by the next creation.
        clock.Now += TimeSpan.FromHours(49);
        await CreatePollAsync(factory, owner, Draft("Later"));
        var rows = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().PollCreations.AsNoTracking().ToListAsync());
        Assert.Equal(clock.Now, Assert.Single(rows).At);
    }

    // ---- 4. favicon -----------------------------------------------------------------------------------

    [Fact]
    public async Task Favicon_is_a_png_branding_image_linked_from_every_page()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var client = factory.CreateClient();
        Assert.Contains("rel=\"icon\" type=\"image/png\" href=\"favicon.png\"", await client.GetStringAsync("/"));

        var jpeg = await Admin(factory, a => a.SaveBrandingAsync(admin, "favicon", new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 1, 2])));
        Assert.Equal((false, "Favicon: choose a PNG file."), (jpeg.Ok, jpeg.Message));
        var big = new byte[256 * 1024 + 1];
        PngBytes.CopyTo(big, 0);
        var tooBig = await Admin(factory, a => a.SaveBrandingAsync(admin, "favicon", new MemoryStream(big)));
        Assert.Equal((false, "Favicon: choose a file of 256 kB or less."), (tooBig.Ok, tooBig.Message));

        Assert.True((await Admin(factory, a => a.SaveBrandingAsync(admin, "favicon", new MemoryStream(PngBytes)))).Ok);
        var version = factory.Services.GetRequiredService<BrandingService>().Get("favicon")!.Version;
        Assert.Contains($"rel=\"icon\" type=\"image/png\" href=\"/branding/favicon?v={version}\"", await client.GetStringAsync("/"));
        var served = await client.GetAsync($"/branding/favicon?v={version}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
    }

    // ---- 5. admin pages without custom CSS ------------------------------------------------------------

    [Fact]
    public async Task Admin_pages_link_the_theme_without_custom_css()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var saved = await Admin(factory, a => a.SaveSettingsAsync(admin,
            new Dictionary<string, string?> { [SettingKeys.ThemeCustomCss] = "body { display: none; }" }, "settings.theme"));
        Assert.True(saved.Ok);
        var client = factory.CreateClient();

        Assert.Contains("display: none", await client.GetStringAsync("/branding/theme.css"));
        var adminCss = await client.GetStringAsync("/branding/theme.css?admin=1");
        Assert.DoesNotContain("display: none", adminCss);
        Assert.Contains("--av-primary", adminCss);

        var theme = factory.Services.GetRequiredService<ThemeService>();
        Assert.Contains($"href=\"{theme.Url}\"", await client.GetStringAsync("/"));
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var adminPage = await (await browser.GetAsync("/admin?tab=settings")).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(theme.AdminUrl)}\"", adminPage);
        Assert.Contains("Not applied on admin, account", adminPage); // in the custom CSS help (DESIGN §22.2)
    }

    // ---- 6. mail retry steps --------------------------------------------------------------------------

    [Fact]
    public async Task Mail_retries_after_1_5_15_60_minutes_then_dead_letters_on_the_fifth_failure()
    {
        var clock = new MutableTime(WholeSecondNow()); // stored times keep milliseconds only
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAsync(factory);
        Assert.True(await factory.Services.GetRequiredService<IMailQueue>().EnqueueAsync("x@example.com", "S", "B", MailType.Test));
        factory.Mail.Fail = true;
        foreach (var minutes in new[] { 1, 5, 15, 60 })
        {
            await factory.SendMailAsync();
            var m = Assert.Single(await OutboxAsync(factory, MailType.Test));
            Assert.Null(m.DeadAt);
            Assert.Equal(clock.Now + TimeSpan.FromMinutes(minutes), m.SendAfter);
            clock.Now = m.SendAfter;
        }
        await factory.SendMailAsync();
        var dead = Assert.Single(await OutboxAsync(factory, MailType.Test));
        Assert.Equal(5, dead.Attempts);
        Assert.NotNull(dead.DeadAt);
        Assert.Equal(MailSender.MaxAttempts, MailSender.Backoff.Length + 1); // every step is reachable
    }

    // ---- 8. notify me when the poll closes ------------------------------------------------------------

    [Fact]
    public async Task Notify_on_close_is_stored_mailed_once_per_account_and_can_be_switched_off()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAndUrlAsync(factory);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Many());
        var alice = await CreateUserAsync(factory, "alice@example.com");
        var unverified = await CreateUnverifiedAsync(factory, "unverified@example.com");
        var anonymous = new VisitorContext(null, "anonymous-key", null);

        // Offered only to signed-in, verified voters.
        var state = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, VisitorContext.ForUser(alice)));
        Assert.True(state!.CanNotify);
        Assert.False((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, anonymous)))!.CanNotify);
        Assert.False((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, VisitorContext.ForUser(unverified))))!.CanNotify);

        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(alice), notify: true)).Succeeded);
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(alice), notify: true)).Succeeded); // a second response
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(unverified), notify: true)).Succeeded);
        Assert.True((await SubmitAsync(factory, id, anonymous, notify: true)).Succeeded);
        var ballots = await BallotsOfAsync(factory, id);
        Assert.Equal(2, ballots.Count(b => b.NotifyOnClose));
        Assert.All(ballots.Where(b => b.UserId != alice), b => Assert.False(b.NotifyOnClose));
        var mine = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, VisitorContext.ForUser(alice)));
        Assert.True(mine!.Responses[0].NotifyOnClose);

        // The page shows the checkbox to Alice, not to an anonymous visitor.
        var aliceBrowser = await LoggedInBrowserAsync(factory, "alice@example.com");
        Assert.Contains("Email me when this poll closes", await (await GetPageAsync(aliceBrowser, $"/p/{id}?r=1")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("Email me when this poll closes", await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync());

        var sweep = factory.Services.GetRequiredService<PollSweepService>();
        Assert.Equal(0, await sweep.NotifyClosedAsync()); // still open
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        await sweep.RunOnceAsync();
        await sweep.RunOnceAsync();
        Assert.Equal(0, await sweep.NotifyClosedAsync());
        var mail = Assert.Single(await OutboxAsync(factory, MailType.ResultsReady));
        Assert.Equal("alice@example.com", mail.To);
        Assert.Contains($"https://vote.example.test/p/{id}/results", mail.TextBody);

        // Unsubscribe: no sign-in, idempotent, one line; a tampered token is 404. A GET only shows the button
        // (AuditFixTests covers it); the POST switches the mails off.
        var link = System.Text.RegularExpressions.Regex.Match(mail.TextBody!, @"https://vote\.example\.test(/notify/off/\S+)").Groups[1].Value;
        var anonymousBrowser = factory.CreateBrowser();
        for (var i = 0; i < 2; i++)
        {
            var off = await AppFactory.SubmitFormAsync(anonymousBrowser, link, "notify-off", new Dictionary<string, string>());
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            Assert.Contains("You will not be emailed about this poll.", await off.Content.ReadAsStringAsync());
        }
        Assert.DoesNotContain(await BallotsOfAsync(factory, id), b => b.NotifyOnClose);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymousBrowser.GetAsync(link + "x")).StatusCode);

        // Reopen and close again without a subscriber: nothing is mailed.
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, false)));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Equal(0, await sweep.NotifyClosedAsync());

        // A day later: reopen, subscribe again, close again: the new close is mailed (two ResultsReady mails in total;
        // a poll is mailed at most once a day, R2-POLLS-3).
        clock.Now += TimeSpan.FromDays(1);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, false)));
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(alice), notify: true, responseNo: 1)).Succeeded);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Equal(1, await sweep.NotifyClosedAsync());
        Assert.Equal(0, await sweep.NotifyClosedAsync());
        Assert.Equal(2, (await OutboxAsync(factory, MailType.ResultsReady)).Count(m => m.To == "alice@example.com"));
    }

    [Fact]
    public async Task Notify_skips_closes_older_than_a_week_and_never_mails_owner_only_results()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAndUrlAsync(factory);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var alice = await CreateUserAsync(factory, "alice@example.com");
        var sweep = factory.Services.GetRequiredService<PollSweepService>();

        // Closed eight days before the sweep first sees it (mail was down): marked, not mailed, never mailed later.
        var stale = await CreatePollAsync(factory, owner, Draft("Stale"));
        Assert.True((await SubmitAsync(factory, stale, VisitorContext.ForUser(alice), notify: true)).Succeeded);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, stale, true)));
        clock.Now += TimeSpan.FromDays(8);
        Assert.Equal(0, await sweep.NotifyClosedAsync());
        clock.Now += TimeSpan.FromDays(2);
        Assert.Equal(0, await sweep.NotifyClosedAsync());

        // Owner-only results (DESIGN.md §25: fixed at creation): the close is marked without mail, even for a flag stored
        // while the results were open to voters.
        var hidden = await CreatePollAsync(factory, owner, Draft("Hidden"));
        Assert.True((await SubmitAsync(factory, hidden, VisitorContext.ForUser(alice), notify: true)).Succeeded);
        Assert.True(PollId.TryDecode(hidden, out var hiddenId));
        await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.Where(p => p.Id == hiddenId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.DefinitionJson,
                PollJson.Serialize(Definition(Draft("Hidden", ResultsVisibility.Creator)) with { TimeZone = "UTC" }))));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, hidden, true)));
        Assert.Equal(0, await sweep.NotifyClosedAsync());
        Assert.Empty(await OutboxAsync(factory, MailType.ResultsReady));
        Assert.NotNull((await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking()
            .SingleAsync(p => p.Id == hiddenId))).ClosedNotifiedAt);
    }

    [Fact]
    public async Task Results_after_closing_are_mailed_when_the_poll_closes()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await ConfigureMailAndUrlAsync(factory);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var alice = await CreateUserAsync(factory, "alice@example.com");
        var sweep = factory.Services.GetRequiredService<PollSweepService>();
        var id = await CreatePollAsync(factory, owner, Draft("Later").With(d => d.ResultsTiming = ResultsTiming.AfterClose));
        Assert.True(await WithScope(factory, async sp =>
            (await sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, VisitorContext.ForUser(alice)))!.CanNotify));
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(alice), notify: true)).Succeeded);
        var before = await WithScope(factory, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(alice)));
        Assert.False(before!.CanSeeResults);
        Assert.Equal(0, await sweep.NotifyClosedAsync());

        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Equal(1, await sweep.NotifyClosedAsync());
        var mail = Assert.Single(await OutboxAsync(factory, MailType.ResultsReady));
        Assert.Equal("alice@example.com", mail.To);
        Assert.Contains($"/p/{id}/results", mail.TextBody);
    }

    [Fact]
    public async Task Notify_checkbox_is_off_without_mail_or_with_creator_only_results()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var alice = await CreateUserAsync(factory, "alice@example.com");
        var open = await CreatePollAsync(factory, owner, Draft(results: ResultsVisibility.Open));
        var hidden = await CreatePollAsync(factory, owner, Draft("Hidden", results: ResultsVisibility.Creator));
        async Task<bool> CanNotify(string id) =>
            (await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, VisitorContext.ForUser(alice))))!.CanNotify;

        Assert.False(await CanNotify(open)); // mail off
        Assert.True((await SubmitAsync(factory, open, VisitorContext.ForUser(alice), notify: true)).Succeeded);
        Assert.False(Assert.Single(await BallotsOfAsync(factory, open)).NotifyOnClose);

        await ConfigureMailAndUrlAsync(factory);
        Assert.True(await CanNotify(open));
        Assert.False(await CanNotify(hidden));
    }

    // ---- 9. ownership transfer ------------------------------------------------------------------------

    [Fact]
    public async Task Admin_transfers_a_poll_to_a_verified_account_only()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var target = await CreateUserAsync(factory, "target@example.com");
        await CreateUnverifiedAsync(factory, "unverified@example.com");
        var gone = await CreateUserAsync(factory, "gone@example.com");
        Assert.True((await Admin(factory, a => a.DeleteUserAsync(admin, gone))).Ok);
        var id = await CreatePollAsync(factory, owner, Draft());

        foreach (var email in new[] { "unverified@example.com", "gone@example.com", "nobody@example.com", "" })
        {
            var refused = await Admin(factory, a => a.TransferPollAsync(admin, id, email));
            Assert.Equal((false, "Enter the email of an account with a verified address."), (refused.Ok, refused.Message));
        }
        Assert.False((await Admin(factory, a => a.TransferPollAsync(admin, id, "owner@example.com"))).Ok);

        var done = await Admin(factory, a => a.TransferPollAsync(admin, id, " Target@Example.com "));
        Assert.True(done.Ok, done.Message);
        Assert.Equal("“Trip” now belongs to target@example.com.", done.Message);
        var polls = factory.Services.CreateScope().ServiceProvider.GetRequiredService<PollService>();
        Assert.Contains(await polls.ListOwnedAsync(target), p => p.EncodedId == id);
        Assert.DoesNotContain(await polls.ListOwnedAsync(owner), p => p.EncodedId == id);
        Assert.NotNull(await polls.GetOverviewAsync(target, id));
        Assert.Null(await polls.GetOverviewAsync(owner, id));
        var log = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AdminActionLogs.AsNoTracking().SingleAsync(l => l.Action == "poll.transfer"));
        Assert.Equal(id, log.TargetId);
        Assert.StartsWith(Telrang.Web.Security.EmailProtection.Prefix, log.Note); // stored encrypted (DESIGN.md §30)
        Assert.Contains("owner@example.com -> target@example.com", Reveal(factory, log.Note));
    }

    // ---- 10. retention ----------------------------------------------------------------------------------

    [Fact]
    public async Task Retention_deletes_polls_closed_longer_than_the_setting_once_a_day()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var closedEarly = await CreatePollAsync(factory, owner, Draft("Early"));
        var open = await CreatePollAsync(factory, owner, Draft("Open"));
        var polls = factory.Services.CreateScope().ServiceProvider.GetRequiredService<PollService>();
        Assert.True((await SubmitAsync(factory, closedEarly, VisitorContext.ForUser(owner), false)).Succeeded);
        Assert.True((await SubmitAsync(factory, open, VisitorContext.ForUser(owner), false)).Succeeded);
        Assert.True(await polls.SetClosedAsync(owner, closedEarly, true));
        var sweep = factory.Services.GetRequiredService<PollSweepService>();
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");

        // Off by default: the share page says nothing and nothing is deleted.
        Assert.DoesNotContain("retention-line", await browser.GetStringAsync($"/polls/{closedEarly}/share"));
        clock.Now += TimeSpan.FromDays(400);
        Assert.Equal(0, await sweep.DeleteExpiredAsync());
        clock.Now -= TimeSpan.FromDays(400);

        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.RetentionDeleteClosedAfterDays] = "7" }, null);
        Assert.Contains("Deleted on", await browser.GetStringAsync($"/polls/{closedEarly}/share"));
        Assert.Contains("Deleted on", await browser.GetStringAsync($"/polls/{open}/share")); // by its auto-close date

        clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromHours(1);
        await sweep.RunOnceAsync(); // retention runs, nothing is due yet
        clock.Now += TimeSpan.FromHours(2);
        await sweep.RunOnceAsync(); // due now, but within a day of the last retention run: skipped
        Assert.Equal(2, (await polls.ListOwnedAsync(owner)).Count);
        clock.Now += TimeSpan.FromDays(1);
        await sweep.RunOnceAsync();
        var left = await polls.ListOwnedAsync(owner);
        Assert.Equal(open, Assert.Single(left).EncodedId);
        Assert.True(PollId.TryDecode(closedEarly, out var gone));
        Assert.False(await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Ballots.AnyAsync(b => b.PollId == gone)));
        Assert.Single(await BallotsOfAsync(factory, open));
    }

    [Fact]
    public void Retention_date_is_the_earlier_close_plus_the_days()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(Retention.DeleteOn(t, t, 0));
        Assert.Null(Retention.DeleteOn(null, null, 7));
        Assert.Equal(t.AddDays(7), Retention.DeleteOn(t, null, 7));
        Assert.Equal(t.AddDays(7), Retention.DeleteOn(null, t, 7));
        Assert.Equal(t.AddDays(7), Retention.DeleteOn(t, t.AddDays(30), 7));
        Assert.Equal(t.AddDays(7), Retention.DeleteOn(t.AddDays(30), t, 7));
    }
}
