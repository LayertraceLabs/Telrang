using System.Net;
using Telrang.Core.Ballots;
using Telrang.Web.Data;
using Telrang.Web.Features.Status;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §28: provider configuration and its refusals, the two migration sets, the Status and Backup tabs, and the
/// connection string kept out of logs, error output and pages. Tests marked PostgresFact need TELRANG_TEST_POSTGRES.
/// </summary>
public class PostgresTests
{
    // Recognisable parts of a connection string that must never be printed.
    private const string SecretPassword = "Pw-7f3a9c-never-shown";
    private const string SecretUser = "telrang_secret_user";
    private const string SecretDatabase = "telrang_secret_db";
    private static readonly string Unreachable =
        $"Host=127.0.0.1;Port=1;Database={SecretDatabase};Username={SecretUser};Password={SecretPassword};Timeout=3";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, v.Value))).Build();

    /// <summary>The string as the app uses it: the pool cap is added when it sets none (pentest R3-DEP-1).</summary>
    private static string WithPoolCap(string cs) =>
        new NpgsqlConnectionStringBuilder(cs) { MaxPoolSize = DatabaseSetup.DefaultMaxPoolSize }.ConnectionString;

    private static void AssertNoSecret(string text)
    {
        Assert.DoesNotContain(SecretPassword, text);
        Assert.DoesNotContain(SecretUser, text);
        Assert.DoesNotContain(SecretDatabase, text);
        Assert.DoesNotContain("127.0.0.1:1", text);
        Assert.DoesNotContain("Port=1", text);
    }

    // ---- configuration ---------------------------------------------------------------------------

    [Fact]
    public void Sqlite_is_the_default_and_keeps_its_file_in_the_data_directory()
    {
        var setup = DatabaseSetup.FromConfiguration(Config(), "/data");
        Assert.Equal(DatabaseProvider.Sqlite, setup.Provider);
        Assert.Equal($"Data Source={Path.Combine("/data", "app.db")}", setup.ConnectionString);
        Assert.Equal("SQLite", setup.ToString());
        Assert.Equal(DatabaseProvider.Sqlite, DatabaseSetup.FromConfiguration(Config(("Database:Provider", "sqlite")), "/data").Provider);
    }

    [Fact]
    public void Unknown_provider_and_postgres_without_a_connection_string_stop_startup()
    {
        var unknown = Assert.Throws<DatabaseConfigException>(() => DatabaseSetup.FromConfiguration(Config(("Database:Provider", "MySql")), "/data"));
        Assert.Equal("Database__Provider must be Sqlite or Postgres.", unknown.Message);
        var missing = Assert.Throws<DatabaseConfigException>(() => DatabaseSetup.FromConfiguration(Config(("Database:Provider", "Postgres")), "/data"));
        Assert.Contains("ConnectionStrings__Default", missing.Message);
    }

    [Fact]
    public void Postgres_takes_the_connection_string_and_never_prints_it()
    {
        var setup = DatabaseSetup.FromConfiguration(Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", Unreachable)), "/data");
        Assert.True(setup.IsPostgres);
        Assert.Equal(WithPoolCap(Unreachable), setup.ConnectionString);
        Assert.Equal("PostgreSQL", setup.ToString());
        Assert.Equal("PostgreSQL", setup.DisplayName);
    }

    [Fact]
    public void An_unreadable_connection_string_is_refused_without_quoting_it()
    {
        var e = Assert.Throws<DatabaseConfigException>(() => DatabaseSetup.FromConfiguration(
            Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", $"Hots=x;Pasword={SecretPassword}")), "/data"));
        AssertNoSecret(e.Message);
        Assert.DoesNotContain("Pasword", e.Message);
    }

    [Fact]
    public void The_file_wins_and_a_missing_or_empty_file_stops_startup()
    {
        var dir = Directory.CreateTempSubdirectory("telrang-cs-");
        try
        {
            var file = Path.Combine(dir.FullName, "cs");
            File.WriteAllText(file, Unreachable + "\n");
            var setup = DatabaseSetup.FromConfiguration(Config(("Database:Provider", "Postgres"),
                ("ConnectionStrings:Default", "Host=ignored;Database=ignored"), ("ConnectionStrings:Default_FILE", file)), "/data");
            Assert.Equal(WithPoolCap(Unreachable), setup.ConnectionString);

            var missing = Assert.Throws<DatabaseConfigException>(() => DatabaseSetup.FromConfiguration(
                Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default_FILE", Path.Combine(dir.FullName, "nope"))), "/data"));
            Assert.Contains("cannot be read", missing.Message);

            File.WriteAllText(file, "  \n");
            var empty = Assert.Throws<DatabaseConfigException>(() => DatabaseSetup.FromConfiguration(
                Config(("Database:Provider", "Postgres"), ("ConnectionStrings:Default", Unreachable), ("ConnectionStrings:Default_FILE", file)), "/data"));
            Assert.Contains("empty file", empty.Message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- startup as a process: exit codes and output -----------------------------------------------

    private static Task<(int ExitCode, string Output)> RunAppAsync(IReadOnlyDictionary<string, string> env, params string[] args) =>
        AppProcess.RunToExitAsync(env, args);

    [Fact]
    public async Task A_bad_provider_or_a_missing_secret_file_ends_the_process_with_exit_code_2()
    {
        var (code, output) = await RunAppAsync(new Dictionary<string, string> { ["Database__Provider"] = "Oracle" });
        Assert.Equal(StartupExit.Configuration, code);
        Assert.Contains("Telrang cannot start: Database__Provider must be Sqlite or Postgres.", output);

        (code, output) = await RunAppAsync(new Dictionary<string, string>
        {
            ["Database__Provider"] = "Postgres",
            ["ConnectionStrings__Default_FILE"] = "/run/secrets/telrang-test-not-there",
        });
        Assert.Equal(StartupExit.Configuration, code);
        Assert.Contains("ConnectionStrings__Default_FILE", output);
    }

    [Fact]
    public async Task An_unreachable_database_ends_the_process_with_exit_code_3_and_no_connection_details()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Unreachable);
            var (code, output) = await RunAppAsync(new Dictionary<string, string>
            {
                ["Database__Provider"] = "Postgres",
                ["ConnectionStrings__Default_FILE"] = file,
            });
            Assert.Equal(StartupExit.Database, code);
            Assert.Contains("the PostgreSQL database cannot be opened or migrated", output);
            AssertNoSecret(output);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [PostgresFact]
    public async Task A_wrong_password_ends_the_process_without_naming_the_user_or_the_database()
    {
        var cs = new NpgsqlConnectionStringBuilder(AppFactory.PostgresServer)
        {
            Username = SecretUser,
            Password = SecretPassword,
            Database = SecretDatabase,
        }.ConnectionString;
        var (code, output) = await RunAppAsync(new Dictionary<string, string>
        {
            ["Database__Provider"] = "Postgres",
            ["ConnectionStrings__Default"] = cs,
        });
        Assert.Equal(StartupExit.Database, code);
        Assert.Contains("SQL state 28P01", output); // invalid password, by state only
        AssertNoSecret(output);
    }

    // ---- logs ------------------------------------------------------------------------------------

    private sealed class CaptureProvider : ILoggerProvider
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose() { }

        public string All => string.Join("\n", Lines);

        private sealed class Capture(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue($"{logLevel} {category} {formatter(state, exception)} {exception}");
        }
    }

    [Fact]
    public async Task A_driver_exception_is_logged_by_type_and_state_only()
    {
        Exception thrown;
        await using (var connection = new NpgsqlConnection(Unreachable))
        {
            thrown = await Assert.ThrowsAnyAsync<NpgsqlException>(() => connection.OpenAsync());
        }
        var wrapped = new DbUpdateException("outer", thrown);
        Assert.True(DatabaseErrors.FromPostgres(wrapped));

        var capture = new CaptureProvider();
        var ring = new LogRing(TimeProvider.System);
        using var inner = new LoggerFactory([capture, new LogRingProvider(ring)]);
        using var factory = new RedactingLoggerFactory(inner);
        factory.CreateLogger("Test").LogError(wrapped, "Saving failed");

        var text = capture.All + "\n" + string.Join("\n", ring.Snapshot().Select(e => e.Message));
        AssertNoSecret(text);
        Assert.Contains("Saving failed", text);
        Assert.Contains("DbUpdateException > NpgsqlException", text);
        Assert.Contains(nameof(RedactedDatabaseException), capture.All);

        // Other exceptions pass unchanged.
        var plain = new InvalidOperationException("kept");
        Assert.Same(plain, DatabaseErrors.Redact(plain));
    }

    /// <summary>A whole session on PostgreSQL (migrations, accounts, votes, the admin pages): no line names the database,
    /// the user or the password of the connection string.</summary>
    [PostgresFact]
    public async Task No_log_line_of_a_postgres_run_holds_connection_details()
    {
        var capture = new CaptureProvider();
        await using var factory = new AppFactory(services: s => s.AddSingleton<ILoggerProvider>(capture));
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "b", null), new RankedBallot(["paris"], []))).Succeeded);
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);

        var cs = new NpgsqlConnectionStringBuilder(factory.PostgresConnectionString);
        var all = capture.All;
        Assert.NotEmpty(capture.Lines);
        Assert.DoesNotContain(cs.Database!, all);
        Assert.DoesNotContain(cs.Password!, all);
        Assert.DoesNotContain($"{cs.Host}:{cs.Port}", all);
        Assert.DoesNotContain("Password=", all);
    }

    // ---- admin: Status and Backup ------------------------------------------------------------------

    [Fact]
    public async Task Status_tab_names_the_provider_and_nothing_about_the_connection()
    {
        await using var factory = new AppFactory();
        await LiveResultsNotifierTests.AdminAsync(factory);
        var snapshot = await WithScope(factory, sp => sp.GetRequiredService<StatusService>().GetAsync());
        Assert.Equal(AppFactory.OnPostgres ? "PostgreSQL" : "SQLite", snapshot.Database);
        Assert.NotNull(snapshot.DatabaseBytes);
        Assert.True(snapshot.DatabaseBytes > 0);

        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var html = await (await browser.GetAsync("/admin?tab=status")).Content.ReadAsStringAsync();
        Assert.Contains(AppFactory.OnPostgres ? "<dd>PostgreSQL · " : "<dd>SQLite · ", html);
        if (AppFactory.OnPostgres)
        {
            var cs = new NpgsqlConnectionStringBuilder(factory.PostgresConnectionString);
            Assert.DoesNotContain(cs.Database!, html);
            Assert.DoesNotContain(cs.Password!, html);
            Assert.DoesNotContain($"Port={cs.Port}", html);
            Assert.DoesNotContain("Username=", html);
        }
    }

    [PostgresFact]
    public async Task Backup_tab_points_to_pg_dump_and_the_download_is_gone()
    {
        await using var factory = new AppFactory();
        await LiveResultsNotifierTests.AdminAsync(factory);
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var settings = await (await browser.GetAsync("/admin?tab=settings")).Content.ReadAsStringAsync();
        Assert.Contains("data-backup=\"postgres\"", settings);
        Assert.Contains("pg_dump", settings);
        Assert.DoesNotContain("action=\"admin/backup\"", settings);

        var token = AppFactory.ScrapeAntiforgeryToken(settings);
        var backup = await browser.PostAsync("/admin/backup", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.NotFound, backup.StatusCode);
    }

    // ---- NUL characters (PostgreSQL text cannot hold U+0000) ----------------------------------------

    [Fact]
    public async Task A_nul_character_in_input_is_no_server_error()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Names(NamesMode.Optional));
        var result = await SubmitRankedAsync(factory, id, new VisitorContext(null, "nul", null), new RankedBallot(["paris"], []), "Ann\0Bee");
        Assert.True(result.Succeeded);
        var name = Assert.Single(await BallotsAsync(factory, id)).DisplayName;
        Assert.Equal(AppFactory.OnPostgres ? "Ann�Bee" : "Ann\0Bee", name);

        var login = await AppFactory.LoginAsync(factory.CreateBrowser(), "o\0@example.com", "x\0y-long-enough-password");
        Assert.NotEqual(HttpStatusCode.InternalServerError, login.StatusCode);
        Assert.Equal("a�b", PostgresNulInterceptor.Clean("a\0b"));
    }

    // ---- the two migration sets ------------------------------------------------------------------------

    private static ApplicationDbContext SqliteContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel().UseSqlite("Data Source=:memory:").Options);

    private static PostgresDbContext PostgresContext() => PostgresDbContext.ForModel();

    [Fact]
    public void Both_migration_sets_are_current()
    {
        using var sqlite = SqliteContext();
        using var postgres = PostgresContext();
        Assert.False(sqlite.Database.HasPendingModelChanges(), "SQLite: run dotnet ef migrations add --context ApplicationDbContext");
        Assert.False(postgres.Database.HasPendingModelChanges(), "PostgreSQL: run dotnet ef migrations add --context PostgresDbContext");
        Assert.DoesNotContain(sqlite.Database.GetMigrations(), postgres.Database.GetMigrations().Contains);
    }

    /// <summary>The migration sets end at the same model: tables, columns (nullability, length, value type), keys, foreign
    /// keys and indexes. Store types differ on purpose (text vs TEXT, timestamptz, uuid).</summary>
    [Fact]
    public void Both_migration_sets_produce_the_same_model()
    {
        using var sqlite = SqliteContext();
        using var postgres = PostgresContext();
        var a = Describe(SnapshotModel(sqlite));
        var b = Describe(SnapshotModel(postgres));
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a, b);
        Assert.Contains(a, l => l.StartsWith("table AspNetUserPasskeys", StringComparison.Ordinal));
        Assert.Contains(a, l => l.StartsWith("table Ballots", StringComparison.Ordinal));
    }

    private static IRelationalModel SnapshotModel(DbContext db)
    {
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        if (snapshot is IMutableModel mutable) snapshot = mutable.FinalizeModel();
        return db.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true).GetRelationalModel();
    }

    private static List<string> Describe(IRelationalModel model)
    {
        static string Filter(string? f) => f?.Replace("\"", "", StringComparison.Ordinal).Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal) ?? "";
        var lines = new List<string>();
        foreach (var t in model.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            lines.Add($"table {t.Name}");
            foreach (var c in t.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var length = c.PropertyMappings.Select(m => m.Property.GetMaxLength()).FirstOrDefault(l => l is not null);
                var type = Nullable.GetUnderlyingType(c.ProviderClrType) ?? c.ProviderClrType;
                // JSON columns map to string on one provider and to the owned type's JSON on the other: compare as JSON.
                var shown = c.PropertyMappings.Count == 0 ? "json" : type.Name;
                lines.Add($"  column {t.Name}.{c.Name} {shown} null={c.IsNullable} max={length}");
            }
            if (t.PrimaryKey is { } pk) lines.Add($"  pk {pk.Name} ({string.Join(",", pk.Columns.Select(c => c.Name))})");
            foreach (var fk in t.ForeignKeyConstraints.OrderBy(f => f.Name, StringComparer.Ordinal))
                lines.Add($"  fk {fk.Name} ({string.Join(",", fk.Columns.Select(c => c.Name))}) -> {fk.PrincipalTable.Name} " +
                          $"({string.Join(",", fk.PrincipalColumns.Select(c => c.Name))}) {fk.OnDeleteAction}");
            foreach (var ix in t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
                lines.Add($"  index {ix.Name} ({string.Join(",", ix.Columns.Select(c => c.Name))}) unique={ix.IsUnique} filter={Filter(ix.Filter)}");
        }
        return lines;
    }
}
