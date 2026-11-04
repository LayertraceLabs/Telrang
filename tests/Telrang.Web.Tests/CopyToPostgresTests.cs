using System.Security.Cryptography;
using System.Text.Json;
using Telrang.Core.Ballots;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §28: <c>copy-to-postgres --from &lt;sqlite file&gt;</c>. Refusals that need no server run everywhere; the
/// round trip and the non-empty target need TELRANG_TEST_POSTGRES.
/// </summary>
public class CopyToPostgresTests
{
    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=x;Username=y;Password=z;Timeout=3";

    private static DatabaseSetup Postgres(string connectionString) => DatabaseSetup.FromConfiguration(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Postgres",
            ["ConnectionStrings:Default"] = connectionString,
        }).Build(), "/unused");

    private static async Task<(int Code, string Out, string Err)> RunAsync(DatabaseSetup target, string dataDir, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CopyToPostgresCommand.RunAsync(args, target, dataDir, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("telrang-copy-").FullName;

    // ---- refusals without a server --------------------------------------------------------------------

    [Fact]
    public async Task Wrong_arguments_print_the_usage_with_exit_code_2()
    {
        var target = Postgres(Unreachable);
        foreach (var args in new[] { Array.Empty<string>(), ["--from"], ["--to", "x"], ["--from", "a", "--from", "b"] })
        {
            var (code, _, err) = await RunAsync(target, "/unused", args);
            Assert.Equal(CopyToPostgresCommand.Usage, code);
            Assert.Contains("Usage: copy-to-postgres --from <sqlite file>", err);
        }
    }

    [Fact]
    public async Task Refuses_without_the_postgres_environment_or_without_the_file()
    {
        var sqlite = DatabaseSetup.FromConfiguration(new ConfigurationBuilder().Build(), "/data");
        var (code, _, err) = await RunAsync(sqlite, "/unused", "--from", "/data/app.db");
        Assert.Equal(CopyToPostgresCommand.Refused, code);
        Assert.Contains("Set Database__Provider=Postgres", err);

        (code, _, err) = await RunAsync(Postgres(Unreachable), "/unused", "--from=/nonexistent/telrang/app.db");
        Assert.Equal(CopyToPostgresCommand.Refused, code);
        Assert.Contains("No file at /nonexistent/telrang/app.db", err);
    }

    [Fact]
    public async Task Refuses_a_file_that_is_no_telrang_database()
    {
        var dir = TempDir();
        try
        {
            var junk = Path.Combine(dir, "junk.db");
            await File.WriteAllBytesAsync(junk, RandomNumberGenerator.GetBytes(4096));
            var (code, _, err) = await RunAsync(Postgres(Unreachable), dir, "--from", junk);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("is not a readable SQLite database", err);

            var empty = Path.Combine(dir, "empty.db");
            await using (var c = new SqliteConnection($"Data Source={empty}"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (x INTEGER)";
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();
            (code, _, err) = await RunAsync(Postgres(Unreachable), dir, "--from", empty);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("holds no Telrang database", err);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Refuses_a_sqlite_file_whose_migrations_are_not_current()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "app.db");
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel().UseSqlite($"Data Source={path}").Options;
            await using (var db = new ApplicationDbContext(options))
            {
                var migrations = db.Database.GetMigrations().ToList();
                await db.GetService<IMigrator>().MigrateAsync(migrations[^3]); // two behind
            }
            SqliteConnection.ClearAllPools();
            var before = await File.ReadAllBytesAsync(path);
            var (code, _, err) = await RunAsync(Postgres(Unreachable), dir, "--from", path);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("2 migration(s) behind this version", err);
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); // opened read-only

            await using (var db = new ApplicationDbContext(options))
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_FromTheFuture', '10.0.0')");
            }
            SqliteConnection.ClearAllPools();
            (code, _, err) = await RunAsync(Postgres(Unreachable), dir, "--from", path);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("written by a newer Telrang version", err);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task An_unreachable_target_fails_with_exit_code_1_and_no_connection_details()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "app.db");
            await using (var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel()
                             .UseSqlite($"Data Source={path}").Options))
            {
                await db.Database.MigrateAsync();
            }
            SqliteConnection.ClearAllPools();
            var secret = "Host=127.0.0.1;Port=1;Database=copy_secret_db;Username=copy_secret_user;Password=copy-secret-pw;Timeout=3";
            var (code, output, err) = await RunAsync(Postgres(secret), dir, "--from", path);
            Assert.Equal(CopyToPostgresCommand.Failed, code);
            Assert.Contains("Copy failed, nothing was committed", err);
            Assert.Contains("Warning: no Data Protection keys", output); // the data directory has no keys folder
            foreach (var part in new[] { "copy_secret_db", "copy_secret_user", "copy-secret-pw", "127.0.0.1:1" })
                Assert.DoesNotContain(part, output + err);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_copy_order_puts_principals_first_and_skips_owned_types()
    {
        using var db = PostgresDbContext.ForModel();
        var order = CopyToPostgresCommand.CopyOrder(db.Model).Select(t => t.GetTableName()).ToList();
        Assert.Equal(order.Distinct().Count(), order.Count); // the passkeys' owned JSON is no table of its own
        Assert.True(order.IndexOf("AspNetUsers") < order.IndexOf("Polls"));
        Assert.True(order.IndexOf("Polls") < order.IndexOf("Ballots"));
        Assert.True(order.IndexOf("Ballots") < order.IndexOf("InviteTokens"));
        Assert.True(order.IndexOf("AspNetRoles") < order.IndexOf("AspNetUserRoles"));
        Assert.Contains("AspNetUserPasskeys", order);
        Assert.DoesNotContain("__EFMigrationsHistory", order);
    }

    // ---- with a server ---------------------------------------------------------------------------------

    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], ["berlin"]);

    /// <summary>A stopped SQLite instance with a bit of everything: accounts (2FA key, roles), polls of all three types,
    /// anonymous, account and invite ballots with names and aliases, a change log, settings, mail, the admin log.</summary>
    private static async Task<(string Owner, string Ranked, string Points, string Dates, string AuthenticatorKey)> PopulateAsync(AppFactory factory)
    {
        await ConfigureMailAsync(factory);
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        await WithScope(factory, async sp =>
        {
            var service = sp.GetRequiredService<AdminService>();
            Assert.True((await service.AddRangeAsync(admin, "203.0.113.0/24", "copied range", null)).Ok);
            Assert.True((await service.AddRangeAsync(admin, "198.51.100.0/24", null, 3)).Ok);
            return 0;
        });
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var key = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(owner))!;
            await users.ResetAuthenticatorKeyAsync(user);
            return (await users.GetAuthenticatorKeyAsync(user))!;
        });
        var voter = await CreateUserAsync(factory, "voter@example.com");

        var ranked = await CreatePollAsync(factory, owner, RankedDraft(ResultsVisibility.AfterVote).Names(NamesMode.Optional).Many());
        var points = await CreatePollAsync(factory, owner, PointsDraft(voters: VoterMode.Invite));
        var dates = await CreatePollAsync(factory, owner, Dates("Meeting", "2030-03-02", "2030-03-03"));
        Assert.True((await SubmitRankedAsync(factory, ranked, new VisitorContext(null, "anon-1", null), ParisFirst, "Anonymous Ann")).Succeeded);
        Assert.True((await SubmitRankedAsync(factory, ranked, new VisitorContext(null, "anon-1", null), ParisFirst, "Anonymous Ann")).Succeeded);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>()
            .SubmitRankedAsync(ranked, VisitorContext.ForUser(voter), new RankedBallot(["rome"], []), "Victor Voter", null, null, true))).Succeeded);
        var invites = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, points, 2, ["Secret Alias"])))!;
        Assert.True(InviteTokens.TryHash(invites[0].Token, out var hash));
        Assert.True((await SubmitPointsAsync(factory, points, new VisitorContext(null, null, hash), new() { ["paris"] = 7, ["rome"] = 3 })).Succeeded);
        var dateKeys = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, dates)))!.Definition.Questions[0].Options
            .Select(o => o.Key).ToList();
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(dates, new VisitorContext(null, "anon-2", null),
            new DatesBallot(new Dictionary<string, Availability> { [dateKeys[0]] = Availability.Yes, [dateKeys[1]] = Availability.Maybe }), null))).Succeeded);

        var draft = PollDraft.FromDefinition((await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, ranked)))!.Definition);
        draft.Questions[0].Options[2].Label = "Berlin Mitte";
        Assert.True((await UpdatePollAsync(factory, owner, ranked, draft)).Succeeded); // a change log row
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LegalPageMarkdown] = "# Copied legal page" }, null);
        await WithScope(factory, async sp =>
        {
            await sp.GetRequiredService<Features.Accounts.AccountService>().RequestPasswordResetAsync("voter@example.com");
            return 0;
        });
        return (owner, ranked, points, dates, key);
    }

    private static async Task<string> ResultsJsonAsync(AppFactory factory, string id, string owner) =>
        await WithScope(factory, async sp =>
        {
            var access = (await sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(owner)))!;
            var results = await sp.GetRequiredService<ResultsService>().ComputeAsync(access);
            return JsonSerializer.Serialize(results);
        });

    private static async Task<Dictionary<string, long>> CountsAsync(AppFactory factory) =>
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var counts = new Dictionary<string, long>();
            foreach (var t in CopyToPostgresCommand.CopyOrder(db.Model))
            {
                var sql = "SELECT COUNT(*) AS \"Value\" FROM \"" + t.GetTableName() + "\"";
                counts[t.GetTableName()!] = await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
            }
            return counts;
        });

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [PostgresFact]
    public async Task Round_trip_copies_every_row_verified_and_the_app_runs_on_the_copy()
    {
        var dir = TempDir();
        var target = "telrang_test_copy_" + Guid.NewGuid().ToString("N");
        await AppFactory.CreatePostgresDatabaseAsync(target);
        var targetCs = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer) { Database = target }.ConnectionString;
        try
        {
            var onSqlite = new Dictionary<string, string?> { ["App:DataDir"] = dir, ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Default"] = "" };
            Dictionary<string, long> before;
            Dictionary<string, string> results;
            (string Owner, string Ranked, string Points, string Dates, string AuthenticatorKey) seeded;
            string voterKeyBefore;
            await using (var source = new AppFactory(onSqlite))
            {
                seeded = await PopulateAsync(source);
                before = await CountsAsync(source);
                results = new()
                {
                    ["ranked"] = await ResultsJsonAsync(source, seeded.Ranked, seeded.Owner),
                    ["points"] = await ResultsJsonAsync(source, seeded.Points, seeded.Owner),
                    ["dates"] = await ResultsJsonAsync(source, seeded.Dates, seeded.Owner),
                };
                voterKeyBefore = (await WithScope(source, sp => sp.GetRequiredService<PollAccessService>()
                    .GetAsync(seeded.Ranked, new VisitorContext(null, "anon-1", null))))!.Voter!.VoterKey!;
            }
            SqliteConnection.ClearAllPools();
            var file = Path.Combine(dir, "app.db");
            var hash = Sha(file);
            var files = Directory.GetFiles(dir).Order().ToList();
            Assert.All(before.Where(c => c.Key is "Polls" or "Ballots" or "PollChanges" or "AdminActionLogs" or "OutboxMessages"
                or "AppSettings" or "BlockedRanges" or "InviteTokens" or "AspNetUserTokens" or "SetupLinks"), c => Assert.True(c.Value > 0, c.Key));

            var (code, output, err) = await RunAsync(Postgres(targetCs), dir, "--from", file);
            Assert.True(code == CopyToPostgresCommand.Ok, output + err);
            Assert.Equal("", err);
            foreach (var (table, rows) in before) Assert.Contains($"  {table}: {rows}\n", output.ReplaceLineEndings("\n"));
            // A summary without data: no address, title, name, alias or setting value.
            foreach (var data in new[] { "owner@example.com", "voter@example.com", "Trip", "Anonymous Ann", "Victor Voter", "Secret Alias",
                         "copied range", "Copied legal page", "203.0.113", "Paris" })
                Assert.DoesNotContain(data, output);
            Assert.DoesNotContain("Warning", output); // the key ring is there
            Assert.Equal(hash, Sha(file)); // the SQLite file is untouched and nothing is deleted or renamed (SQLite may add -wal/-shm)
            Assert.Subset(Directory.GetFiles(dir).ToHashSet(), files.ToHashSet());

            // The app on the copy: same data directory (keys), PostgreSQL.
            var onPostgres = new Dictionary<string, string?> { ["App:DataDir"] = dir, ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = targetCs };
            await using var copy = new AppFactory(onPostgres, registrationOpen: false);
            // The app creates its writer lock row on PostgreSQL at startup (DbWrites, pentest R3-DEP-1); the rest is the copy.
            Assert.Equal(new Dictionary<string, long>(before) { ["WriterLocks"] = 1 }, await CountsAsync(copy));
            // Addresses are copied encrypted as they are (DESIGN.md §30); the key directory came along, so they read back.
            Assert.Equal(0, await WithScope(copy, sp => sp.GetRequiredService<ApplicationDbContext>().Database
                .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"AspNetUsers\" WHERE \"Email\" LIKE '%@%' OR \"NormalizedEmail\" LIKE '%@%'").SingleAsync()));
            Assert.Equal("owner@example.com", await WithScope(copy, async sp =>
            {
                var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
                return await users.GetEmailAsync((await users.FindByEmailAsync("OWNER@example.com"))!);
            }));
            Assert.Equal(results["ranked"], await ResultsJsonAsync(copy, seeded.Ranked, seeded.Owner));
            Assert.Equal(results["points"], await ResultsJsonAsync(copy, seeded.Points, seeded.Owner));
            Assert.Equal(results["dates"], await ResultsJsonAsync(copy, seeded.Dates, seeded.Owner));
            var again = (await WithScope(copy, sp => sp.GetRequiredService<PollAccessService>()
                .GetAsync(seeded.Ranked, new VisitorContext(null, "anon-1", null))))!;
            Assert.Equal(voterKeyBefore, again.Voter!.VoterKey); // voter keys survive (key ring moved along)
            Assert.True(again.HasBallot);
            Assert.Equal(seeded.AuthenticatorKey, await WithScope(copy, async sp =>
            {
                var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
                return (await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(seeded.Owner))!))!;
            }));
            Assert.Equal("# Copied legal page", copy.Settings.Current.LegalPageMarkdown);
            var browser = await LoggedInBrowserAsync(copy, "owner@example.com"); // password hash and security stamp copied
            Assert.Equal(System.Net.HttpStatusCode.OK, (await browser.GetAsync("/polls")).StatusCode);

            // Sequences continue after the copied ids: new admin log rows and identity claims insert.
            var admin = await LiveResultsNotifierTests.AdminAsync(copy, "admin2@example.com");
            Assert.True((await WithScope(copy, sp => sp.GetRequiredService<AdminService>().AddRangeAsync(admin, "192.0.2.0/24", null, null))).Ok);
            Assert.Equal(before["AdminActionLogs"] + 1, (await CountsAsync(copy))["AdminActionLogs"]);
            Assert.True((await SubmitRankedAsync(copy, seeded.Ranked, new VisitorContext(null, "new-voter", null), ParisFirst)).Succeeded);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            SqliteConnection.ClearAllPools();
            await AppFactory.DropPostgresDatabaseAsync(target);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Pentest R3-DEP-12b: a SQLite file the running app has open is refused; once the app stops it is not.</summary>
    [SqliteOnlyFact]
    public async Task Refuses_a_sqlite_file_the_app_has_open()
    {
        // The app as its own process: SQLite's locks are per process, as between the service and the command.
        var dir = TempDir();
        var file = Path.Combine(dir, "app.db");
        var port = AppProcess.FreePort();
        using var process = System.Diagnostics.Process.Start(AppProcess.StartInfo(dir,
            new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" }, []))!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                try
                {
                    if ((await client.GetAsync("/healthz")).IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                Assert.False(process.HasExited, "the app ended");
                Assert.True(DateTime.UtcNow < deadline, "the app did not answer");
                await Task.Delay(200);
            }
            Assert.True(CopyToPostgresCommand.SqliteInUse(file));
            var (code, _, err) = await RunAsync(Postgres(Unreachable), dir, "--from", file);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("is in use", err);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.False(CopyToPostgresCommand.SqliteInUse(file));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Pentest R3-DEP-12a: a target holding another application's tables, or a migration history that is not Telrang's,
    /// is refused before anything is migrated into it.
    /// </summary>
    [PostgresFact]
    public async Task Refuses_another_applications_database_before_migrating()
    {
        var dir = TempDir();
        var target = "telrang_test_copy_" + Guid.NewGuid().ToString("N");
        await AppFactory.CreatePostgresDatabaseAsync(target);
        var targetCs = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer) { Database = target }.ConnectionString;
        async Task ExecAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(targetCs);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        async Task<long> TablesAsync()
        {
            await using var connection = new NpgsqlConnection(targetCs);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        try
        {
            await using (var source = new AppFactory(new Dictionary<string, string?> { ["App:DataDir"] = dir, ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Default"] = "" }))
            {
                await CreateUserAsync(source, "o@example.com");
            }
            SqliteConnection.ClearAllPools();
            var file = Path.Combine(dir, "app.db");

            await ExecAsync("CREATE TABLE invoices (id int)");
            var (code, _, err) = await RunAsync(Postgres(targetCs), dir, "--from", file);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("not Telrang's (invoices)", err);
            Assert.Equal(1, await TablesAsync()); // nothing migrated into it

            await ExecAsync("DROP TABLE invoices; CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32)); " +
                            "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20240101000000_OtherApp', '9.0.0')");
            (code, _, err) = await RunAsync(Postgres(targetCs), dir, "--from", file);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("migration history that is not this Telrang version's", err);
            Assert.Equal(1, await TablesAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            SqliteConnection.ClearAllPools();
            await AppFactory.DropPostgresDatabaseAsync(target);
            Directory.Delete(dir, recursive: true);
        }
    }

    [PostgresFact]
    public async Task Refuses_a_target_that_holds_telrang_rows_and_leaves_it_alone()
    {
        var dir = TempDir();
        var target = "telrang_test_copy_" + Guid.NewGuid().ToString("N");
        await AppFactory.CreatePostgresDatabaseAsync(target);
        var targetCs = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer) { Database = target }.ConnectionString;
        try
        {
            await using (var source = new AppFactory(new Dictionary<string, string?> { ["App:DataDir"] = dir, ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Default"] = "" }))
            {
                var owner = await CreateUserAsync(source, "o@example.com");
                await CreatePollAsync(source, owner, RankedDraft());
            }
            SqliteConnection.ClearAllPools();
            var file = Path.Combine(dir, "app.db");
            Assert.Equal(CopyToPostgresCommand.Ok, (await RunAsync(Postgres(targetCs), dir, "--from", file)).Code);

            var (code, _, err) = await RunAsync(Postgres(targetCs), dir, "--from", file);
            Assert.Equal(CopyToPostgresCommand.Refused, code);
            Assert.Contains("already holds Telrang rows", err);

            // A target the app was started on once (it seeds the roles) is refused too.
            var started = "telrang_test_copy_" + Guid.NewGuid().ToString("N");
            await AppFactory.CreatePostgresDatabaseAsync(started);
            try
            {
                var startedCs = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer) { Database = started }.ConnectionString;
                await using (var app = new AppFactory(new Dictionary<string, string?> { ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = startedCs }))
                {
                    _ = app.Services; // starts: migrates and seeds
                }
                (code, _, err) = await RunAsync(Postgres(startedCs), dir, "--from", file);
                Assert.Equal(CopyToPostgresCommand.Refused, code);
                Assert.Contains("do not start the app on it before the copy", err);
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                await AppFactory.DropPostgresDatabaseAsync(started);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            SqliteConnection.ClearAllPools();
            await AppFactory.DropPostgresDatabaseAsync(target);
            Directory.Delete(dir, recursive: true);
        }
    }
}
