using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telrang.Core.Ballots;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Upgrade fixtures (DESIGN.md §32 "Upgrade safety"): a data directory made by one release (<c>app.db</c>, <c>keys/</c>,
/// <c>branding/</c>), the same data as a PostgreSQL dump (<c>postgres.sql</c>) and what it holds (<c>manifest.json</c>:
/// accounts with their passwords and 2FA secrets, polls with their expected results, invite tokens, a voter cookie,
/// row counts). <see cref="MigrationUpgradeTests"/> starts the current app on each fixture and checks the data is intact.
/// Made by <see cref="UpgradeFixtureGenerator"/>; how and when: <c>Fixtures/upgrade/README.md</c>.
/// </summary>
internal static partial class UpgradeFixtures
{
    public const string ManifestFile = "manifest.json";
    public const string PostgresDump = "postgres.sql";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "Telrang.Web.Tests", "Fixtures", "upgrade");
        }
    }

    /// <summary>Fixture directory names (release versions), oldest first.</summary>
    public static IReadOnlyList<string> Names() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Where(d => File.Exists(Path.Combine(d, ManifestFile))).Select(d => Path.GetFileName(d)!)
                .OrderBy(n => Version.TryParse(n!.Split('-')[0], out var v) ? v : new Version(0, 0)).ThenBy(n => n, StringComparer.Ordinal)
                .ToList()!
            : [];

    public static UpgradeManifest Manifest(string name) =>
        JsonSerializer.Deserialize<UpgradeManifest>(File.ReadAllText(Path.Combine(Root, name, ManifestFile)), Json)!;

    /// <summary>A private copy of the fixture's data directory (the test migrates it; the fixture stays as committed).</summary>
    public static string CopyDataDir(string name)
    {
        var target = Path.Combine(Path.GetTempPath(), "telrang-upgrade-" + Guid.NewGuid().ToString("N"));
        CopyTree(Path.Combine(Root, name), target, file => Path.GetFileName(file) is not (ManifestFile or PostgresDump));
        return target;
    }

    public static void CopyTree(string from, string to, Func<string, bool> include)
    {
        CreatePrivateDirectory(to);
        foreach (var file in Directory.GetFiles(from).Where(include)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), _ => true);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The newest migration of each set in this build.</summary>
    public static (string Sqlite, string Postgres) CodeSchemas()
    {
        using var sqlite = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel()
            .UseSqlite("Data Source=:memory:").Options);
        using var postgres = PostgresDbContext.ForModel();
        return (sqlite.Database.GetMigrations().Last(), postgres.Database.GetMigrations().Last());
    }

    // ---- PostgreSQL: the dump ---------------------------------------------------------------------------------------

    /// <summary>
    /// Restores <c>postgres.sql</c> (plain SQL from <c>pg_dump --inserts</c>, no COPY) into the new empty database
    /// <paramref name="database"/> on the test server. psql meta-commands (<c>\restrict</c>) are dropped; the rest runs
    /// as one batch on an unpooled connection (the dump empties <c>search_path</c> for its session).
    /// </summary>
    public static async Task RestorePostgresAsync(string name, string database)
    {
        var sql = string.Join('\n', File.ReadAllLines(Path.Combine(Root, name, PostgresDump)).Where(l => !l.StartsWith('\\')));
        var cs = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer) { Database = database, Pooling = false, CommandTimeout = 300 };
        await using var connection = new NpgsqlConnection(cs.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // ---- what is compared ---------------------------------------------------------------------------------------------

    /// <summary>A poll's results in words that stay comparable across releases (keys, scores, counts; no labels or layout).</summary>
    public static List<string> Digest(PollResults r)
    {
        var lines = new List<string> { $"voters={r.VoterCount}" };
        foreach (var row in r.Ranked?.Rows ?? [])
            lines.Add(FormattableString.Invariant($"{row.Key} score={row.Score:R} first={row.FirstChoiceCount} bans={row.BanCount} ranks={string.Join(',', row.RankHistogram)}"));
        foreach (var row in r.Points?.Rows ?? [])
            lines.Add(FormattableString.Invariant($"{row.Key} total={row.Total} mean={row.Mean:R} supporters={row.SupporterCount} max={row.Max}"));
        foreach (var row in r.Dates?.Rows ?? [])
            lines.Add(FormattableString.Invariant($"{row.Key} yes={row.Yes} maybe={row.Maybe} no={row.No} place={row.Place}"));
        return lines;
    }

    public static async Task<List<string>> DigestAsync(AppFactory factory, string encodedId, string ownerId) =>
        await WithScope(factory, async sp =>
        {
            var access = (await sp.GetRequiredService<PollAccessService>().GetAsync(encodedId, VisitorContext.ForUser(ownerId)))!;
            return Digest(await sp.GetRequiredService<ResultsService>().ComputeAsync(access));
        });

    public static async Task<Dictionary<string, long>> CountsAsync(AppFactory factory) =>
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var t in CopyToPostgresCommand.CopyOrder(db.Model))
            {
                var sql = "SELECT COUNT(*) AS \"Value\" FROM \"" + t.GetTableName() + "\"";
                counts[t.GetTableName()!] = await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
            }
            return counts.ToDictionary();
        });

    public static async Task<List<ChangeEntry>> ChangesAsync(AppFactory factory, string encodedId, string ownerId) =>
        await WithScope(factory, async sp =>
        {
            var access = (await sp.GetRequiredService<PollAccessService>().GetAsync(encodedId, VisitorContext.ForUser(ownerId)))!;
            var page = await sp.GetRequiredService<PollService>().ListChangesAsync(access);
            return page.Entries.Select(c => new ChangeEntry(c.Kind.ToString(), c.Label, c.OldLabel, c.BallotCount)).ToList();
        });

    // ---- the vote form over HTTP (the cookie voter) -----------------------------------------------------------------

    [GeneratedRegex("name=\"fid\" value=\"([^\"]+)\"")]
    private static partial Regex FormIdRegex();

    [GeneratedRegex("<input type=\"hidden\" name=\"r\" value=\"(\\d+)\"")]
    private static partial Regex ResponseRegex();

    public static async Task<string> VotePageAsync(HttpClient client, string path)
    {
        var response = await GetPageAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Posts the vote form of <c>/p/{id}</c> as a browser does (see VoteFormTests); expects the redirect to the summary.</summary>
    public static async Task VoteOverHttpAsync(HttpClient client, string id, params (string Key, string Value)[] fields)
    {
        var response = await GetPageAsync(client, $"/p/{id}");
        var html = await response.Content.ReadAsStringAsync();
        var form = fields.ToDictionary(f => f.Key, f => f.Value);
        form["__RequestVerificationToken"] = AppFactory.ScrapeAntiforgeryToken(html);
        form["_handler"] = VotePage.VoteForm;
        if (AppFactory.ScrapeFormStamp(html) is { } stamp) form.TryAdd(FormGuard.StampField, stamp);
        if (FormIdRegex().Match(html) is { Success: true } f) form.TryAdd(BallotForm.FormIdField, f.Groups[1].Value);
        if (ResponseRegex().Match(html) is { Success: true } r) form.TryAdd(BallotForm.ResponseField, r.Groups[1].Value);
        var saved = await client.PostAsync($"/p/{id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.EndsWith($"/p/{id}?saved=1", saved.Headers.Location!.ToString());
    }
}

// ---- the manifest ----------------------------------------------------------------------------------------------------

/// <summary>What a fixture holds and what the upgraded app must still give back. Test data only: no real secrets.</summary>
internal sealed class UpgradeManifest
{
    /// <summary>The release the fixture was made with (also the directory name).</summary>
    public required string Version { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The newest SQLite migration applied to <c>app.db</c>.</summary>
    public required string SqliteSchema { get; init; }

    /// <summary>The newest PostgreSQL migration in <c>postgres.sql</c> (null: the fixture has no dump).</summary>
    public string? PostgresSchema { get; init; }

    public required List<FixtureAccount> Accounts { get; init; }
    public required List<FixturePoll> Polls { get; init; }
    public required List<FixtureInvite> Invites { get; init; }

    /// <summary>The <c>vk</c> cookie of an anonymous voter, as the browser holds it (protected with the key ring).</summary>
    public required FixtureCookieVoter CookieVoter { get; init; }

    /// <summary>The account with "Email me when this poll closes" on the <see cref="CookieVoter"/>'s poll.</summary>
    public required string NotifyEmail { get; init; }

    /// <summary>The account created by the admin and not set up yet (no email).</summary>
    public required string PendingAccountId { get; init; }

    /// <summary>Invited by email and not set up yet.</summary>
    public required string PendingInviteEmail { get; init; }

    public required string SmtpPassword { get; init; }
    public required string AppDisplayName { get; init; }
    public required Dictionary<string, string> Settings { get; init; }
    public required List<string> BlockedRanges { get; init; }
    public required List<string> BrandingFiles { get; init; }

    /// <summary>Recipients of the mail still queued (sent by the test after the upgrade).</summary>
    public required List<string> QueuedMailTo { get; init; }

    public required int SentMail { get; init; }
    public required Dictionary<string, long> RowCounts { get; init; }
}

internal sealed record FixtureAccount(string Id, string Email, string? DisplayName, bool Admin, string? AuthenticatorKey, List<string> RecoveryCodes);

/// <param name="Name">A name for the test messages (ranked, points, dates, closed).</param>
/// <param name="Results">The <see cref="UpgradeFixtures.Digest"/> computed when the fixture was made.</param>
internal sealed record FixturePoll(string Name, string Id, string Type, string OwnerEmail, string Title, string VoterMode, bool Closed,
    List<string> OptionKeys, List<string> Results, List<ChangeEntry> Changes);

internal sealed record ChangeEntry(string Kind, string Label, string? OldLabel, int? BallotCount);

internal sealed record FixtureInvite(string Poll, string Token, string? Alias, bool Used, string? ClaimedByEmail);

internal sealed record FixtureCookieVoter(string Poll, string Cookie);

/// <summary>
/// Makes the upgrade fixture of the current code: runs only with <c>TELRANG_MAKE_UPGRADE_FIXTURE=&lt;version&gt;</c>
/// (and, for <c>postgres.sql</c>, <c>TELRANG_TEST_POSTGRES</c> plus a <c>pg_dump</c>, see the README next to the fixtures).
/// </summary>
public class UpgradeFixtureGenerator
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("TELRANG_MAKE_UPGRADE_FIXTURE") is { Length: > 0 } v ? v : null;

    public sealed class GeneratorFactAttribute : FactAttribute
    {
        public GeneratorFactAttribute()
        {
            if (Target is null) Skip = "Makes an upgrade fixture: set TELRANG_MAKE_UPGRADE_FIXTURE=<version> (Fixtures/upgrade/README.md).";
        }
    }

    private const string Password = TestUtil.Password;

    [GeneratorFact]
    public async Task Make_the_upgrade_fixture()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", Target!);
        var work = Path.Combine(Path.GetTempPath(), "telrang-fixture-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(UpgradeFixtures.Root, Target!);
        try
        {
            var onSqlite = new Dictionary<string, string?> { ["App:DataDir"] = work, ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Default"] = "" };
            UpgradeManifest manifest;
            await using (var app = new AppFactory(onSqlite))
            {
                manifest = await PopulateAsync(app);
            }
            SqliteConnection.ClearAllPools();
            await using (var c = new SqliteConnection($"Data Source={Path.Combine(work, "app.db")};Pooling=False"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=DELETE; VACUUM;";
                await cmd.ExecuteNonQueryAsync();
            }

            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            UpgradeFixtures.CopyTree(work, output, f => Path.GetFileName(f) == "app.db");
            var tmp = Path.Combine(output, "tmp");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);

            string? postgresSchema = null;
            if (AppFactory.OnPostgres)
            {
                postgresSchema = UpgradeFixtures.CodeSchemas().Postgres;
                await DumpPostgresAsync(output);
            }
            manifest = new UpgradeManifest
            {
                Version = Target!, GeneratedAt = manifest.GeneratedAt, SqliteSchema = manifest.SqliteSchema, PostgresSchema = postgresSchema,
                Accounts = manifest.Accounts, Polls = manifest.Polls, Invites = manifest.Invites, CookieVoter = manifest.CookieVoter,
                NotifyEmail = manifest.NotifyEmail, PendingAccountId = manifest.PendingAccountId, PendingInviteEmail = manifest.PendingInviteEmail,
                SmtpPassword = manifest.SmtpPassword, AppDisplayName = manifest.AppDisplayName, Settings = manifest.Settings,
                BlockedRanges = manifest.BlockedRanges, BrandingFiles = manifest.BrandingFiles, QueuedMailTo = manifest.QueuedMailTo,
                SentMail = manifest.SentMail, RowCounts = manifest.RowCounts,
            };
            await File.WriteAllTextAsync(Path.Combine(output, UpgradeFixtures.ManifestFile), JsonSerializer.Serialize(manifest, UpgradeFixtures.Json) + "\n");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// The SQLite fixture moved with <c>copy-to-postgres</c> (the supported way onto PostgreSQL) into a new database, then
    /// dumped as plain SQL. <c>TELRANG_PG_DUMP</c> is the pg_dump command (default <c>pg_dump</c>, run with the test
    /// server's host, port, user and password in PG* variables); e.g. <c>docker exec telrang-pg pg_dump -U postgres</c>.
    /// </summary>
    private static async Task DumpPostgresAsync(string fixtureDir)
    {
        var database = "telrang_fixture_" + Guid.NewGuid().ToString("N");
        await AppFactory.CreatePostgresDatabaseAsync(database);
        try
        {
            var server = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer);
            var target = DatabaseSetup.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Postgres",
                    ["ConnectionStrings:Default"] = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer) { Database = database }.ConnectionString,
                }).Build(), fixtureDir);
            var outText = new StringWriter();
            var errText = new StringWriter();
            var code = await CopyToPostgresCommand.RunAsync(["--from", Path.Combine(fixtureDir, "app.db")], target, fixtureDir, outText, errText);
            Assert.True(code == CopyToPostgresCommand.Ok, outText.ToString() + errText);
            NpgsqlConnection.ClearAllPools();
            SqliteConnection.ClearAllPools();

            var command = (Environment.GetEnvironmentVariable("TELRANG_PG_DUMP") is { Length: > 0 } c ? c : "pg_dump")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var start = new ProcessStartInfo(command[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in command.Skip(1)) start.ArgumentList.Add(arg);
            foreach (var arg in new[] { "--no-owner", "--no-privileges", "--no-comments", "--inserts", "--rows-per-insert=100", "--quote-all-identifiers", database })
                start.ArgumentList.Add(arg);
            start.Environment["PGHOST"] = server.Host;
            start.Environment["PGPORT"] = server.Port.ToString(CultureInfo.InvariantCulture);
            start.Environment["PGUSER"] = server.Username;
            start.Environment["PGPASSWORD"] = server.Password;
            using var process = Process.Start(start)!;
            var dump = process.StandardOutput.ReadToEndAsync();
            var err = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, "pg_dump failed: " + await err);
            // Lines that change with every dump and say nothing about the data (pg_dump's per-run \restrict key).
            var lines = (await dump).ReplaceLineEndings("\n").Split('\n')
                .Where(l => !l.StartsWith("\\restrict", StringComparison.Ordinal) && !l.StartsWith("\\unrestrict", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(fixtureDir, UpgradeFixtures.PostgresDump), string.Join('\n', lines));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await AppFactory.DropPostgresDatabaseAsync(database);
        }
    }

    private static Task<T> Admin<T>(AppFactory app, Func<AdminService, Task<T>> action) =>
        WithScope(app, sp => action(sp.GetRequiredService<AdminService>()));

    private static async Task<string> SetUpAccountAsync(AppFactory app, string email, string? displayName)
    {
        var id = await CreateUserAsync(app, email);
        if (displayName is not null)
        {
            await WithScope(app, async sp =>
            {
                var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
                var user = (await users.FindByIdAsync(id))!;
                user.DisplayName = displayName;
                Assert.True((await users.UpdateAsync(user)).Succeeded);
                return 0;
            });
        }
        return id;
    }

    /// <summary>Everything a live instance holds, made through the app's services and pages.</summary>
    private static async Task<UpgradeManifest> PopulateAsync(AppFactory app)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var accounts = new List<FixtureAccount>();

        // Admin and the instance settings (through AdminService, so the admin log has them).
        var adminId = await SetUpAccountAsync(app, "admin@example.com", "Ada Admin");
        var admin = await WithScope(app, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(adminId))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
        accounts.Add(new FixtureAccount(adminId, "admin@example.com", "Ada Admin", true, null, []));
        const string smtpPassword = "fixture-smtp-password";
        var settings = new Dictionary<string, string>
        {
            [SettingKeys.DisplayName] = "Fixture Votes",
            [SettingKeys.PublicUrl] = "https://localhost",
            [SettingKeys.LegalPageMarkdown] = "# Legal\n\nFixture legal page.",
            [SettingKeys.HomeWelcomeMarkdown] = "Welcome to the **fixture**.",
            [SettingKeys.LimitPollDurationDays] = "0", // the fixture's open polls stay open, however old the fixture gets
            [SettingKeys.LimitVoterName] = "50",
            [SettingKeys.ThemeLightBase] = "#1d4e89",
            [SettingKeys.ThemeLightHighlight] = "#f2a541",
            [SettingKeys.ThemeLayout] = "sidebar",
        };
        Assert.True((await Admin(app, a => a.SaveSettingsAsync(admin, settings.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), "settings.general"))).Ok);
        var mail = new Dictionary<string, string?>
        {
            [SettingKeys.MailHost] = "smtp.example.test",
            [SettingKeys.MailPort] = "587",
            [SettingKeys.MailSecurity] = "StartTls",
            [SettingKeys.MailUsername] = "mailer",
            [SettingKeys.MailPassword] = smtpPassword,
            [SettingKeys.MailFromAddress] = "vote@example.test",
            [SettingKeys.MailFromName] = "Fixture Votes",
        };
        var save = await Admin(app, a => a.SaveSettingsAsync(admin, mail, "settings.mail"));
        Assert.True(save.Ok, save.Message);
        Assert.Equal(smtpPassword, app.Settings.GetMailPassword());
        foreach (var (key, value) in mail.Where(kv => kv.Key != SettingKeys.MailPassword)) settings[key] = value!;
        Assert.True((await Admin(app, a => a.AddRangeAsync(admin, "203.0.113.0/24", "fixture range", null))).Ok);
        Assert.True((await Admin(app, a => a.AddRangeAsync(admin, "198.51.100.0/24", null, 3650))).Ok);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Enumerable.Repeat((byte)7, 120)).ToArray();
        Assert.True((await Admin(app, a => a.SaveBrandingAsync(admin, "logo", new MemoryStream(png)))).Ok);

        // Accounts: an owner with 2FA and recovery codes, voters, a pending account and a pending email invite.
        var ownerId = await SetUpAccountAsync(app, "owner@example.com", "Olive Owner");
        var (totpKey, codes) = await WithScope(app, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(ownerId))!;
            await users.ResetAuthenticatorKeyAsync(user);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
            var recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 4))!.ToList();
            return ((await users.GetAuthenticatorKeyAsync(user))!, recovery);
        });
        accounts.Add(new FixtureAccount(ownerId, "owner@example.com", "Olive Owner", false, totpKey, codes));
        var voterId = await SetUpAccountAsync(app, "voter@example.com", "Victor Voter");
        accounts.Add(new FixtureAccount(voterId, "voter@example.com", "Victor Voter", false, null, []));
        var plainId = await SetUpAccountAsync(app, "Plain.User@Example.com", null);
        accounts.Add(new FixtureAccount(plainId, "Plain.User@Example.com", null, false, null, []));
        var created = await Admin(app, a => a.CreateAccountAsync(admin, "https://localhost/"));
        Assert.True(created.Ok, created.Message);
        var pendingId = await WithScope(app, sp => sp.GetRequiredService<ApplicationDbContext>().Users
            .Where(u => u.PendingSetup && u.Email == null).Select(u => u.Id).SingleAsync());
        Assert.True((await Admin(app, a => a.InviteAsync(admin, "invitee@example.com"))).Ok);

        // Ranked, public, names optional, many responses: an anonymous voter over HTTP (vk cookie), an account voter who
        // asked to be told when it closes, then option changes after the ballots (removed, renamed, added, and an option
        // added back under its old label gets a new key, _2).
        var polls = new List<FixturePoll>();
        var ranked = await CreatePollAsync(app, ownerId, RankedDraft(ResultsVisibility.AfterVote).Names(NamesMode.Optional).Many()
            .Describe("Where shall we go? *Markdown* description."));
        var (browser, jar) = app.CreateBrowserWithJar();
        await UpgradeFixtures.VoteOverHttpAsync(browser, ranked, ("o.rome", "1"), ("o.paris", "2"), ("b.berlin", "1"), ("name", "Cookie Carla"));
        var vk = jar.GetCookies(new Uri("https://localhost/"))[PollCookies.VoterCookie]!.Value;
        Assert.True((await WithScope(app, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(ranked, VisitorContext.ForUser(voterId),
            new RankedBallot(["paris", "berlin"], []), "Victor", null, null, notifyOnClose: true))).Succeeded);
        Assert.True((await SubmitRankedAsync(app, ranked, new VisitorContext(null, "fixture-anon-2", null), new RankedBallot(["berlin"], ["rome"]))).Succeeded);
        var draft = PollDraft.FromDefinition((await WithScope(app, sp => sp.GetRequiredService<PollService>().GetForEditAsync(ownerId, ranked)))!.Definition);
        draft.Questions[0].Options.RemoveAll(o => o.Key == "berlin");
        draft.Questions[0].Options.Single(o => o.Key == "rome").Label = "Roma";
        draft.Questions[0].Options.Add(new OptionDraft { Label = "Lisbon", Description = "By the sea." });
        draft.Title = "Trip 2027";
        var edit = await UpdatePollAsync(app, ownerId, ranked, draft);
        Assert.True(edit.Succeeded, edit.Message);
        draft = PollDraft.FromDefinition((await WithScope(app, sp => sp.GetRequiredService<PollService>().GetForEditAsync(ownerId, ranked)))!.Definition);
        draft.Questions[0].Options.Add(new OptionDraft { Label = "Berlin" });
        Assert.True((await UpdatePollAsync(app, ownerId, ranked, draft)).Succeeded);
        var rankedKeys = (await WithScope(app, sp => sp.GetRequiredService<PollService>().GetForEditAsync(ownerId, ranked)))!.Definition.Questions[0].Options
            .Select(o => o.Key).ToList();
        Assert.Equal(["paris", "rome", "_1", "_2"], rankedKeys); // options added by an edit get new keys, never a retired one
        Assert.True((await SubmitRankedAsync(app, ranked, new VisitorContext(null, "fixture-anon-3", null), new RankedBallot(["_2", "_1"], ["paris"]), "Late Larry")).Succeeded);
        Assert.True((await SubmitRankedAsync(app, ranked, new VisitorContext(null, "fixture-anon-3", null), new RankedBallot(["_1"], []), "Late Larry again")).Succeeded);

        // Points, invite only, with aliases: one link used anonymously, one claimed by an account, one unused.
        var points = await CreatePollAsync(app, ownerId, PointsDraft(ResultsVisibility.AfterVote, VoterMode.Invite, budget: 12)
            .Q(q => q.MaxPerOption = 8).Names(NamesMode.Required));
        var invites = (await WithScope(app, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(ownerId, points, 3, ["Aunt Alice", "Uncle Bob"])))!;
        Assert.Equal(3, invites.Count);
        Assert.True(InviteTokens.TryHash(invites[0].Token, out var hash0));
        Assert.True(InviteTokens.TryHash(invites[1].Token, out var hash1));
        Assert.True((await SubmitPointsAsync(app, points, new VisitorContext(null, null, hash0), new() { ["paris"] = 8, ["rome"] = 4 }, "Alice")).Succeeded);
        Assert.True((await SubmitPointsAsync(app, points, new VisitorContext(voterId, null, hash1), new() { ["rome"] = 6, ["berlin"] = 6 }, "Bob")).Succeeded);
        var inviteRows = new List<FixtureInvite>
        {
            new("points", invites[0].Token, invites[0].Alias, true, null),
            new("points", invites[1].Token, invites[1].Alias, true, "voter@example.com"),
            new("points", invites[2].Token, invites[2].Alias, false, null),
        };

        // Dates, signed-in voters only, with a close date far ahead (lifted max duration) and a time zone.
        var dates = await CreatePollAsync(app, ownerId, Dates("Meeting", "2030-03-02", "2030-03-03 19:00", "2030-03-04 09:00-11:30")
            .Voters(VoterMode.Accounts).Results(ResultsVisibility.AfterVote).With(x => x.ResultsTiming = ResultsTiming.AfterClose).Zone("Europe/Amsterdam").Closes("2035-12-31"));
        var dateKeys = (await WithScope(app, sp => sp.GetRequiredService<PollService>().GetForEditAsync(ownerId, dates)))!.Definition.Questions[0].Options
            .Select(o => o.Key).ToList();
        foreach (var (who, answers) in new[] { (voterId, new[] { Availability.Yes, Availability.Maybe, Availability.No }),
                     (plainId, new[] { Availability.Yes, Availability.Yes, Availability.Maybe }), (ownerId, new[] { Availability.No, Availability.Yes, Availability.Yes }) })
        {
            var ballot = new DatesBallot(dateKeys.Zip(answers).ToDictionary(x => x.First, x => x.Second));
            Assert.True((await WithScope(app, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(dates, VisitorContext.ForUser(who), ballot, null))).Succeeded);
        }

        // A closed poll (by its owner), by another owner.
        var closed = await CreatePollAsync(app, voterId, Ranked("Lunch", "Pizza", "Sushi", "Salad").Results(ResultsVisibility.Open).With(x => x.ResultsTiming = ResultsTiming.AfterClose).Q(q => q.Bans = 0));
        Assert.True((await SubmitRankedAsync(app, closed, VisitorContext.ForUser(plainId), new RankedBallot(["sushi", "pizza"], []))).Succeeded);
        Assert.True((await SubmitRankedAsync(app, closed, new VisitorContext(null, "fixture-anon-4", null), new RankedBallot(["pizza"], []))).Succeeded);
        Assert.True((await WithScope(app, sp => sp.GetRequiredService<PollService>().ChangeClosedAsync(voterId, closed, true))).Ok);

        foreach (var (name, id, owner, ownerEmail) in new[] { ("ranked", ranked, ownerId, "owner@example.com"), ("points", points, ownerId, "owner@example.com"),
                     ("dates", dates, ownerId, "owner@example.com"), ("closed", closed, voterId, "voter@example.com") })
        {
            var poll = await WithScope(app, async sp => (await sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(owner)))!);
            polls.Add(new FixturePoll(name, id, poll.Poll.Type.ToString(), ownerEmail, poll.Poll.Title, poll.Poll.VoterMode.ToString(), poll.IsClosed,
                poll.Definition.Questions[0].Options.Select(o => o.Key).ToList(), await UpgradeFixtures.DigestAsync(app, id, owner),
                await UpgradeFixtures.ChangesAsync(app, id, owner)));
        }
        Assert.NotEmpty(polls[0].Changes);
        Assert.True(polls[3].Closed);

        // Mail: what is queued so far is sent; two more stay queued.
        await WithScope(app, async sp =>
        {
            await sp.GetRequiredService<AccountService>().RequestPasswordResetAsync("Plain.User@Example.com");
            return 0;
        });
        var sent = await app.SendMailAsync();
        Assert.True(sent >= 2, $"sent {sent}");
        await WithScope(app, async sp =>
        {
            await sp.GetRequiredService<AccountService>().RequestPasswordResetAsync("voter@example.com");
            return 0;
        });
        Assert.True((await Admin(app, a => a.SendTestMailAsync(admin))).Ok);
        var queued = await WithScope(app, async sp => (await sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.SentAt == null && m.DeadAt == null).ToListAsync()).Select(m => Reveal(app, m.To)!).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(2, queued.Count);

        var counts = await UpgradeFixtures.CountsAsync(app);
        foreach (var table in new[] { "Polls", "Ballots", "InviteTokens", "PollChanges", "OutboxMessages", "AdminActionLogs", "AppSettings",
                     "BlockedRanges", "SetupLinks", "AspNetUserTokens", "AspNetUserRoles" })
            Assert.True(counts[table] > 0, table);

        return new UpgradeManifest
        {
            Version = "", GeneratedAt = generatedAt, SqliteSchema = UpgradeFixtures.CodeSchemas().Sqlite,
            Accounts = accounts, Polls = polls, Invites = inviteRows, CookieVoter = new FixtureCookieVoter("ranked", vk),
            NotifyEmail = "voter@example.com", PendingAccountId = pendingId, PendingInviteEmail = "invitee@example.com",
            SmtpPassword = smtpPassword, AppDisplayName = settings[SettingKeys.DisplayName], Settings = settings,
            BlockedRanges = ["203.0.113.0/24", "198.51.100.0/24"], BrandingFiles = ["logo"], QueuedMailTo = queued, SentMail = sent,
            RowCounts = counts,
        };
    }
}
