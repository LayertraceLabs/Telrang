using System.Net;
using Telrang.Core.Ballots;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Migrations applied to databases that hold data, as an upgrade meets them (not only to empty ones). The fixture tests
/// (DESIGN.md §32 "Upgrade safety") start the current app on the data of every release kept under <c>Fixtures/upgrade/</c>
/// (<see cref="UpgradeFixtures"/>): on SQLite from its <c>app.db</c>, and with TELRANG_TEST_POSTGRES on PostgreSQL restored
/// from its <c>postgres.sql</c>, both with the release's key ring. Migrations and the startup steps run as on a live
/// instance; then everything the release stored must still be there and still work.
/// </summary>
public class MigrationUpgradeTests
{
    [Fact]
    public async Task Outbox_with_sent_mail_upgrades_and_loses_only_the_sent_bodies()
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
            // The schema before OutboxBodiesNullable: TextBody NOT NULL. Before the fix that migration's UPDATE ran ahead of
            // SQLite's table rebuild and failed on any outbox with a sent or dead mail.
            await db.GetService<IMigrator>().MigrateAsync("20261031062036_FormEditorQuestions");
            foreach (var (id, sent, dead) in new (string, long?, long?)[] { ("sent", 1, null), ("dead", null, 1), ("queued", null, null) })
            {
                var text = $"body {id}";
                var html = $"<p>{id}</p>";
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"OutboxMessages\" (\"Id\", \"To\", \"Subject\", \"TextBody\", \"HtmlBody\", \"Type\", \"CreatedAt\", \"SendAfter\", \"Attempts\", \"SentAt\", \"DeadAt\") VALUES ({id}, 'a@example.com', 'S', {text}, {html}, 'PasswordReset', 0, 0, 0, {sent}, {dead})");
            }
        }
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.MigrateAsync();
            var bodies = await db.Database.SqlQueryRaw<Row>("SELECT \"Id\", \"TextBody\", \"HtmlBody\" FROM \"OutboxMessages\"").ToDictionaryAsync(r => r.Id);
            Assert.Null(bodies["sent"].TextBody);
            Assert.Null(bodies["sent"].HtmlBody);
            Assert.Null(bodies["dead"].TextBody);
            Assert.Equal("body queued", bodies["queued"].TextBody);
            Assert.Equal("<p>queued</p>", bodies["queued"].HtmlBody);
        }
        SqliteConnection.ClearAllPools();
    }

    private sealed record Row(string Id, string? TextBody, string? HtmlBody);

    // ---- release fixtures ---------------------------------------------------------------------------------------------

    public static TheoryData<string> Fixtures() => new(UpgradeFixtures.Names());

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Release_fixture_upgrades_on_sqlite(string fixture)
    {
        var manifest = UpgradeFixtures.Manifest(fixture);
        var dir = UpgradeFixtures.CopyDataDir(fixture);
        try
        {
            Assert.Equal(manifest.SqliteSchema, await LastSqliteMigrationAsync(Path.Combine(dir, "app.db")));
            var onSqlite = new Dictionary<string, string?> { ["App:DataDir"] = dir, ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Default"] = "" };
            await using var app = new AppFactory(onSqlite, UpgradeDay(manifest), registrationOpen: false);
            await VerifyAsync(app, manifest);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [PostgresTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Release_fixture_upgrades_on_postgres(string fixture)
    {
        var manifest = UpgradeFixtures.Manifest(fixture);
        Assert.True(manifest.PostgresSchema is not null, $"Fixture {fixture} has no {UpgradeFixtures.PostgresDump}: make it with TELRANG_TEST_POSTGRES set (Fixtures/upgrade/README.md).");
        var dir = UpgradeFixtures.CopyDataDir(fixture);
        var database = "telrang_test_upgrade_" + Guid.NewGuid().ToString("N");
        await AppFactory.CreatePostgresDatabaseAsync(database);
        try
        {
            await UpgradeFixtures.RestorePostgresAsync(fixture, database);
            var cs = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer) { Database = database, MaxPoolSize = 20 }.ConnectionString;
            await using (var before = new NpgsqlConnection(cs))
            {
                await before.OpenAsync();
                await using var last = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1", before);
                Assert.Equal(manifest.PostgresSchema, (string?)await last.ExecuteScalarAsync());
            }
            File.Delete(Path.Combine(dir, "app.db")); // the PostgreSQL instance has no SQLite file; only the data directory's keys
            var onPostgres = new Dictionary<string, string?> { ["App:DataDir"] = dir, ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = cs };
            await using var app = new AppFactory(onPostgres, UpgradeDay(manifest), registrationOpen: false);
            await VerifyAsync(app, manifest);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await AppFactory.DropPostgresDatabaseAsync(database);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Fixtures are made by releases this code can read: every migration a fixture applied still exists here (a migration
    /// is never renamed or removed once released), and a release build (a <c>v*</c> tag in CI, where GitHub sets
    /// GITHUB_REF_TYPE/GITHUB_REF_NAME) has the fixture of its own version, made with its schema.
    /// </summary>
    [Fact]
    public void Fixtures_match_the_migrations_and_a_tagged_release_has_its_own()
    {
        var names = UpgradeFixtures.Names();
        Assert.NotEmpty(names);
        var (sqlite, postgres) = UpgradeFixtures.CodeSchemas();
        using var sqliteDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel().UseSqlite("Data Source=:memory:").Options);
        using var postgresDb = PostgresDbContext.ForModel();
        var sqliteIds = sqliteDb.Database.GetMigrations().ToHashSet();
        var postgresIds = postgresDb.Database.GetMigrations().ToHashSet();
        foreach (var name in names)
        {
            var manifest = UpgradeFixtures.Manifest(name);
            Assert.Equal(name, manifest.Version);
            Assert.True(sqliteIds.Contains(manifest.SqliteSchema), $"Fixture {name}: migration {manifest.SqliteSchema} is gone from Data/Migrations.");
            Assert.True(manifest.PostgresSchema is null || postgresIds.Contains(manifest.PostgresSchema),
                $"Fixture {name}: migration {manifest.PostgresSchema} is gone from Data/PostgresMigrations.");
        }

        if (Environment.GetEnvironmentVariable("GITHUB_REF_TYPE") != "tag") return;
        // A pre-release (1.2.0-rc.1) is checked against the fixture of the release it leads to (1.2.0).
        var version = Environment.GetEnvironmentVariable("GITHUB_REF_NAME")?.TrimStart('v').Split('-')[0];
        Assert.True(names.Contains(version!), $"Release {version} has no upgrade fixture: run the generator (tests/Telrang.Web.Tests/Fixtures/upgrade/README.md), commit it, tag again.");
        var own = UpgradeFixtures.Manifest(version!);
        Assert.True(own.SqliteSchema == sqlite && own.PostgresSchema == postgres,
            $"The fixture of {version} was made with an older schema ({own.SqliteSchema}, {own.PostgresSchema}; the code has {sqlite}, {postgres}). Make it again.");
    }

    private static async Task<string> LastSqliteMigrationAsync(string file)
    {
        await using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1";
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The upgrade happens an hour after the fixture was made, whenever the test runs: what the fixture holds ages (queued
    /// mail is dropped after 7 days), and the test is about the upgrade, not about that.
    /// </summary>
    private static Action<IServiceCollection> UpgradeDay(UpgradeManifest m) =>
        s => s.AddSingleton<TimeProvider>(new TestUtil.MutableTime(m.GeneratedAt.AddHours(1)));

    /// <summary>The checks, the same on both providers. The app has started on the fixture: migrations and startup steps ran.</summary>
    private static async Task VerifyAsync(AppFactory app, UpgradeManifest m)
    {
        // Schema current, no row lost or added by the upgrade (the writer lock row is the app's own).
        Assert.Empty(await WithScope(app, async sp => (await sp.GetRequiredService<ApplicationDbContext>().Database.GetPendingMigrationsAsync()).ToList()));
        var counts = await UpgradeFixtures.CountsAsync(app);
        foreach (var (table, rows) in m.RowCounts.Where(c => c.Key != "WriterLocks" && counts.ContainsKey(c.Key)))
            Assert.True(rows == counts[table], $"{table}: {rows} rows in the fixture, {counts[table]} after the upgrade");

        // Mail queued by the release still goes out (recipients and bodies decrypt with the key ring).
        Assert.Equal(m.QueuedMailTo.Count, await app.SendMailAsync());
        Assert.Equal(m.QueuedMailTo, app.Mail.Sent.Select(s => s.To).Order(StringComparer.Ordinal).ToList());
        Assert.All(app.Mail.Sent, s => Assert.False(string.IsNullOrWhiteSpace(s.TextBody)));

        // Accounts: addresses encrypted at rest, found by address, readable; passwords, 2FA, recovery codes still work.
        Assert.Equal(0, await WithScope(app, sp => sp.GetRequiredService<ApplicationDbContext>().Users
            .CountAsync(u => u.Email!.Contains("@") || u.NormalizedEmail!.Contains("@") || u.UserName!.Contains("@"))));
        var ids = new Dictionary<string, string>();
        HttpClient? ownerBrowser = null;
        foreach (var account in m.Accounts)
        {
            ids[account.Email] = account.Id;
            var (found, email, displayName, isAdmin, storedKey) = await WithScope(app, async sp =>
            {
                var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
                var user = (await users.FindByEmailAsync(account.Email.ToUpperInvariant()))!;
                return (user.Id, await users.GetEmailAsync(user), user.DisplayName, await users.IsInRoleAsync(user, AppRoles.Admin),
                    await users.GetAuthenticatorKeyAsync(user));
            });
            Assert.Equal(account.Id, found);
            Assert.Equal(account.Email, email);
            Assert.Equal(account.DisplayName, displayName);
            Assert.Equal(account.Admin, isAdmin);
            var browser = app.CreateBrowser();
            var login = await AppFactory.LoginAsync(browser, account.Email, Password);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            if (account.AuthenticatorKey is { } key)
            {
                Assert.Equal(key, storedKey);
                Assert.Contains("LoginWith2fa", login.Headers.Location!.ToString());
                var done = await AppFactory.SubmitFormAsync(browser, login.Headers.Location!.ToString(), "login-with-2fa",
                    new Dictionary<string, string> { ["Input.TwoFactorCode"] = Phase5aTests.Totp(key) });
                Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
                Assert.DoesNotContain("/Account/", done.Headers.Location!.ToString());

                var second = app.CreateBrowser();
                Assert.Contains("LoginWith2fa", (await AppFactory.LoginAsync(second, account.Email, Password)).Headers.Location!.ToString());
                var recovered = await AppFactory.SubmitFormAsync(second, "/Account/LoginWithRecoveryCode", "login-with-recovery-code",
                    new Dictionary<string, string> { ["Input.RecoveryCode"] = account.RecoveryCodes[0] });
                Assert.Equal(HttpStatusCode.Redirect, recovered.StatusCode);
                Assert.DoesNotContain("/Account/", recovered.Headers.Location!.ToString());
                Assert.Equal(account.RecoveryCodes.Count - 1, await WithScope(app, async sp =>
                {
                    var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
                    return await users.CountRecoveryCodesAsync((await users.FindByIdAsync(account.Id))!);
                }));
            }
            else
            {
                Assert.DoesNotContain("/Account/", login.Headers.Location!.ToString());
            }
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/polls")).StatusCode);
            if (account.Admin) Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);
            if (m.Polls.Any(p => p.OwnerEmail == account.Email && account.AuthenticatorKey is not null)) ownerBrowser = browser;
        }
        Assert.NotNull(ownerBrowser);

        // Pending accounts: the admin's new setup link completes the one without an address; the invite is found by address.
        var adminId = m.Accounts.First(a => a.Admin).Id;
        var admin = await WithScope(app, async sp => await sp.GetRequiredService<SignInManager<ApplicationUser>>()
            .CreateUserPrincipalAsync((await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(adminId))!));
        var renewed = await WithScope(app, sp => sp.GetRequiredService<AdminService>().RenewSetupLinkAsync(admin, m.PendingAccountId, "https://localhost/"));
        Assert.True(renewed.Ok, renewed.Message);
        var setup = await WithScope(app, sp => sp.GetRequiredService<AccountService>()
            .CompleteSetupAsync(renewed.Link!.Split('/').Last(), "newcomer@example.com", Password));
        Assert.True(setup.Result.Succeeded, string.Join(", ", setup.Result.Errors.Select(e => e.Description)));
        Assert.Equal(m.PendingAccountId, setup.User!.Id);
        Assert.True(await WithScope(app, async sp =>
            (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(m.PendingInviteEmail))?.PendingSetup == true));

        // Polls: each opens through PollAccessService with its settings, and computes the results the release computed.
        var now = DateTimeOffset.UtcNow;
        foreach (var poll in m.Polls)
        {
            var ownerId = ids[poll.OwnerEmail];
            var access = await WithScope(app, sp => sp.GetRequiredService<PollAccessService>().GetAsync(poll.Id, VisitorContext.ForUser(ownerId)));
            Assert.True(access is { IsOwner: true, CanView: true, CanSeeResults: true }, poll.Name);
            Assert.Equal(poll.Title, access!.Poll.Title);
            Assert.Equal(poll.Type, access.Poll.Type.ToString());
            Assert.Equal(poll.VoterMode, access.Poll.VoterMode.ToString());
            Assert.Equal(poll.Closed, access.IsClosed);
            Assert.Equal(poll.Closed, PollState.IsClosed(access.Poll, now));
            Assert.Equal(poll.OptionKeys, access.Definition.Questions[0].Options.Select(o => o.Key).ToList());
            Assert.Equal(poll.Results, await UpgradeFixtures.DigestAsync(app, poll.Id, ownerId));
            Assert.Equal(poll.Changes, await UpgradeFixtures.ChangesAsync(app, poll.Id, ownerId));
            if (poll.OwnerEmail == m.Accounts.First(a => a.AuthenticatorKey is not null).Email)
            {
                Assert.Equal(HttpStatusCode.OK, (await ownerBrowser!.GetAsync($"/p/{poll.Id}/results")).StatusCode);
                var ballotsCsv = await ownerBrowser.GetAsync($"/polls/{poll.Id}/export.csv");
                Assert.Equal(HttpStatusCode.OK, ballotsCsv.StatusCode);
                var csv = await ballotsCsv.Content.ReadAsStringAsync();
                foreach (var alias in m.Invites.Where(i => i.Poll == poll.Name && i.Used && i.Alias is not null)) Assert.Contains(alias.Alias!, csv);
            }
        }

        // The change log renders for every viewer of the public poll, labels and all.
        var ranked = m.Polls.Single(p => p.Name == "ranked");
        var page = await UpgradeFixtures.VotePageAsync(app.CreateBrowser(), $"/p/{ranked.Id}");
        foreach (var change in ranked.Changes)
        {
            Assert.Contains(change.Label, page);
            if (change.OldLabel is not null) Assert.Contains(change.OldLabel, page);
        }

        // The anonymous voter's cookie (protected with the release's key ring) still finds their ballot.
        var cookieVoter = m.Polls.Single(p => p.Name == m.CookieVoter.Poll);
        var (voterBrowser, jar) = app.CreateBrowserWithJar();
        jar.Add(new Uri("https://localhost/"), new Cookie(PollCookies.VoterCookie, m.CookieVoter.Cookie) { Path = "/" });
        var visitor = VisitorFrom(app, jar, cookieVoter.Id);
        Assert.NotNull(visitor.LinkVoterKey);
        var mine = await WithScope(app, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(cookieVoter.Id, visitor));
        Assert.Single(mine!.Responses);
        Assert.Contains("Cookie Carla", await UpgradeFixtures.VotePageAsync(voterBrowser, $"/p/{cookieVoter.Id}?r=1&edit=1"));

        // Invite links: a used one is still its voter's ballot, a claimed one the account's, an unused one can vote; the
        // aliases are kept for the owner.
        foreach (var invite in m.Invites)
        {
            var poll = m.Polls.Single(p => p.Name == invite.Poll);
            Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
            var who = invite.ClaimedByEmail is { } email ? new VisitorContext(ids[email], null, hash) : new VisitorContext(null, null, hash);
            var access = (await WithScope(app, sp => sp.GetRequiredService<PollAccessService>().GetAsync(poll.Id, who)))!;
            Assert.True(access.CanVote, $"invite {invite.Alias}: {access.ReasonIfDenied}");
            Assert.Equal(invite.Used, access.HasBallot);
            if (invite.ClaimedByEmail is null)
                Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(app.CreateBrowser(), $"/p/{poll.Id}?t={invite.Token}")).StatusCode);
            if (!invite.Used)
            {
                var options = access.Definition.Questions[0].Options;
                Assert.True((await SubmitPointsAsync(app, poll.Id, who, new() { [options[0].Key] = 1 }, "New Nora")).Succeeded);
            }
        }
        var aliases = await WithScope(app, sp => sp.GetRequiredService<PollService>().ListInvitesAsync(ids[m.Polls.Single(p => p.Name == "points").OwnerEmail],
            m.Polls.Single(p => p.Name == "points").Id));
        Assert.Equal(m.Invites.Select(i => i.Alias).Where(a => a is not null).Order(StringComparer.Ordinal),
            aliases!.Select(i => i.Alias).Where(a => a is not null).Order(StringComparer.Ordinal));

        // Settings: the stored SMTP password decrypts; the rest as saved; blocked ranges and branding in force.
        Assert.Equal(m.SmtpPassword, app.Settings.GetMailPassword());
        foreach (var (key, value) in m.Settings) Assert.Equal(value, app.Settings.Current.Raw.GetValueOrDefault(key));
        Assert.Equal(m.AppDisplayName, app.Settings.AppName);
        var ranges = app.Services.GetRequiredService<BlockedRangeService>();
        foreach (var cidr in m.BlockedRanges) Assert.True(ranges.IsBlocked(IPAddress.Parse(cidr.Split('/')[0][..^1] + "7")), cidr);
        foreach (var name in m.BrandingFiles) Assert.NotNull(app.Services.GetRequiredService<BrandingService>().Get(name));
        var css = await (await app.CreateBrowser().GetAsync("/branding/theme.css")).Content.ReadAsStringAsync();
        Assert.Contains(m.Settings[SettingKeys.ThemeLightBase], css);

        // The admin log: rows readable, the notes with addresses decrypt.
        var log = await WithScope(app, sp => sp.GetRequiredService<AdminService>().ListLogAsync(admin, 0));
        Assert.True(log!.Value.Total >= m.RowCounts["AdminActionLogs"]);
        Assert.All(log.Value.Rows, r => Assert.NotEqual(EmailProtection.UnreadableLabel, r.AdminEmail));
        Assert.Contains(log.Value.Rows, r => r.Note == m.PendingInviteEmail);

        // "Email me when this poll closes" survives: closing the poll mails the subscriber.
        var notifyOwner = ids[cookieVoter.OwnerEmail];
        Assert.True((await WithScope(app, sp => sp.GetRequiredService<PollService>().ChangeClosedAsync(notifyOwner, cookieVoter.Id, true))).Ok);
        Assert.Equal(1, await app.Services.GetRequiredService<PollSweepService>().NotifyClosedAsync());
        await app.SendMailAsync();
        Assert.Contains(app.Mail.Sent, s => s.To == m.NotifyEmail && s.Subject.Contains(ranked.Title, StringComparison.Ordinal));

        // And new writes work on the upgraded data.
        var dates = m.Polls.Single(p => p.Name == "dates");
        var revised = await WithScope(app, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(dates.Id, VisitorContext.ForUser(ids[m.NotifyEmail]),
            new DatesBallot(new Dictionary<string, Availability> { [dates.OptionKeys[0]] = Availability.Maybe }), null));
        Assert.True(revised.Succeeded, string.Join(", ", revised.Errors));
        var fresh = await CreatePollAsync(app, ids[ranked.OwnerEmail], RankedDraft());
        Assert.True((await SubmitRankedAsync(app, fresh, new VisitorContext(null, "after-upgrade", null), new RankedBallot(["paris"], []))).Succeeded);
    }
}
