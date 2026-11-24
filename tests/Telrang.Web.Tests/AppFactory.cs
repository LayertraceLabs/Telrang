using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Telrang.Web.Tests;

/// <summary>
/// Boots the real app against a throw-away data directory (SQLite file + keys). With <c>TELRANG_TEST_POSTGRES</c> set to a
/// connection string for a server where the tests may create and drop databases, the app runs on PostgreSQL instead: one
/// new database per factory, dropped on dispose (DESIGN.md §28).
/// </summary>
/// <param name="overrides">Configuration values (applied after the defaults below).</param>
/// <param name="services">Service replacements, applied after Program's registrations (e.g. a fake TimeProvider).</param>
/// <param name="registrationOpen">Sets the DB setting Registration.Enabled after startup (the app default is closed).</param>
public sealed partial class AppFactory(IReadOnlyDictionary<string, string?>? overrides = null, Action<IServiceCollection>? services = null,
    bool registrationOpen = true)
    : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "telrang-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>The server the Web tests run against instead of SQLite (<c>TELRANG_TEST_POSTGRES</c>), or null.</summary>
    public static string? PostgresServer { get; } =
        Environment.GetEnvironmentVariable("TELRANG_TEST_POSTGRES") is { Length: > 0 } cs ? cs : null;

    public static bool OnPostgres => PostgresServer is not null;

    /// <summary>
    /// This factory's PostgreSQL database (created on first use), or null on SQLite. A test that "restarts" the app passes
    /// the same <c>App:DataDir</c> to a second factory: on PostgreSQL both use one database, named after that directory and
    /// dropped when the test run ends (the directory stands for the whole instance, as on SQLite).
    /// </summary>
    public string? PostgresDatabase => !OnPostgres ? null
        : overrides?.GetValueOrDefault("App:DataDir") is { Length: > 0 } dir ? "telrang_test_dir_" + Hash(dir)
        : ownDatabase;

    private readonly string ownDatabase = "telrang_test_" + Guid.NewGuid().ToString("N");

    private bool SharedDatabase => PostgresDatabase != ownDatabase;

    private static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32];

    private static readonly List<string> SharedDatabases = [];

    /// <summary>The connection string the app uses on PostgreSQL (null on SQLite).</summary>
    public string? PostgresConnectionString => PostgresDatabase is null ? null
        : new NpgsqlConnectionStringBuilder(PostgresServer) { Database = PostgresDatabase, MaxPoolSize = 20 }.ConnectionString;

    private bool databaseCreated;

    /// <summary>Captures every message the background sender would deliver (see <see cref="FakeMailTransport"/>).</summary>
    public FakeMailTransport Mail { get; } = new();

    /// <summary>The setup-key singleton. Only the hash is kept, so tests call <see cref="SetupKey.Issue"/> for a usable key.</summary>
    public SetupKey SetupKey => Services.GetRequiredService<SetupKey>();

    public SettingsService Settings => Services.GetRequiredService<SettingsService>();

    /// <summary>One pass of the outbox sender (the 10 s loop is off in tests).</summary>
    public Task<int> SendMailAsync() => Services.GetRequiredService<MailSender>().ProcessDueAsync();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (registrationOpen)
        {
            host.Services.GetRequiredService<SettingsService>()
                .SetAsync(new Dictionary<string, string?> { [SettingKeys.RegistrationEnabled] = "true" }, null)
                .GetAwaiter().GetResult();
        }
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // UseSetting (not ConfigureAppConfiguration) so Program sees the values before Build().
        builder.UseSetting("App:DataDir", DataDir);
        // No operator file (DESIGN.md §29), whatever /etc/telrang or %ProgramData%\Telrang hold on the test machine.
        builder.UseSetting("TELRANG_CONFIG", "none");
        builder.UseSetting("ConnectionStrings:Default", "");
        if (PostgresDatabase is not null)
        {
            CreatePostgresDatabase();
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", PostgresConnectionString);
        }
        builder.UseSetting("Mail:BackgroundSender", "false");
        builder.UseSetting("Polls:BackgroundSweep", "false"); // tests call PollSweepService directly
        // Tests post forms right after loading them; the 2 s minimum has its own test (Phase5b2Tests).
        builder.UseSetting("Abuse:FormMinSeconds", "0");
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(s => s.AddSingleton<IMailTransport>(Mail));
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }

    /// <summary>Cookie-handling client that does not follow redirects, over https so Secure cookies flow.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
    });

    /// <summary>Like <see cref="CreateBrowser"/> but exposes the cookie jar (to read poll cookies in tests).</summary>
    public (HttpClient Client, CookieContainer Jar) CreateBrowserWithJar()
    {
        var jar = new CookieContainer();
        var client = CreateDefaultClient(new Uri("https://localhost"), new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(jar));
        return (client, jar);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (databaseCreated && !SharedDatabase) await DropPostgresDatabaseAsync(PostgresDatabase!);
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void CreatePostgresDatabase()
    {
        if (databaseCreated) return;
        if (SharedDatabase)
        {
            lock (SharedDatabases)
            {
                if (SharedDatabases.Contains(PostgresDatabase!)) return;
                if (SharedDatabases.Count == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => DropSharedDatabases();
                SharedDatabases.Add(PostgresDatabase!);
            }
        }
        CreatePostgresDatabaseAsync(PostgresDatabase!).GetAwaiter().GetResult();
        databaseCreated = true;
    }

    private static void DropSharedDatabases()
    {
        lock (SharedDatabases)
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var name in SharedDatabases)
            {
                try { DropPostgresDatabaseAsync(name).Wait(TimeSpan.FromSeconds(10)); }
                catch (AggregateException) { } // the server is gone already: nothing to clean
            }
        }
    }

    /// <summary>Creates an empty database on the test server (names are generated, never input).</summary>
    public static async Task CreatePostgresDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(PostgresServer);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DropPostgresDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(PostgresServer);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    // ---- helpers for Blazor SSR forms ------------------------------------------------------

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();

    public static string ScrapeAntiforgeryToken(string html) =>
        AntiforgeryRegex().Match(html) is { Success: true } m
            ? WebUtility.HtmlDecode(m.Groups[1].Value)
            : throw new InvalidOperationException("No antiforgery token in page:\n" + html);

    [GeneratedRegex("name=\"_fts\" value=\"([^\"]+)\"")]
    private static partial Regex FormStampRegex();

    /// <summary>The signed form stamp (FormGuard) of the first guarded form in the page, or null.</summary>
    public static string? ScrapeFormStamp(string html) =>
        FormStampRegex().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    /// <summary>GETs <paramref name="path"/>, scrapes its antiforgery token and POSTs the named Blazor form.</summary>
    public static async Task<HttpResponseMessage> SubmitFormAsync(HttpClient client, string path, string formName,
        IDictionary<string, string> fields, string? tokenSourcePath = null)
    {
        var page = await client.GetAsync(tokenSourcePath ?? path);
        var html = await page.Content.ReadAsStringAsync();
        var token = ScrapeAntiforgeryToken(html);
        var form = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = formName,
        };
        if (ScrapeFormStamp(html) is { } stamp) form.TryAdd("_fts", stamp);
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    public static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password) =>
        SubmitFormAsync(client, "/Account/Register", "register", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
        });

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        SubmitFormAsync(client, "/Account/Login", "login", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        });
}
