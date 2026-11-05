using Telrang.Web.Components;
using Telrang.Web.Components.Account;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Status;
using Telrang.Web.Features.Voting;
using Telrang.Web.Hosting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// `healthcheck` (DESIGN.md §29): the Docker HEALTHCHECK; asks the running app, starts nothing.
if (args is [HealthCheckCommand.Name, ..])
{
    return await HealthCheckCommand.RunAsync(args[1..], Console.Out);
}
// `copy-to-postgres --from <sqlite file>` (DESIGN.md §28) runs instead of the server; its arguments are not configuration.
var command = args is [CopyToPostgresCommand.Name, ..] ? args[1..] : null;
var platform = HostPlatform.Current();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = command is null ? args : [],
    // A service starts in another working directory (System32 for the Windows SCM): appsettings and wwwroot are found
    // next to the program instead.
    ContentRootPath = platform.IsService ? AppContext.BaseDirectory : null,
});
var config = builder.Configuration;
var isDev = builder.Environment.IsDevelopment();

// ---- Install formats (DESIGN.md §29): operator file, service managers --------------------------
// Sources, highest first: command line, environment, the operator file (TELRANG_CONFIG or the platform default),
// the shipped appsettings*.json.
OperatorConfigFile? operatorFile;
try
{
    operatorFile = OperatorConfig.Apply(config, platform);
}
catch (OperatorConfigException e)
{
    ServiceHosting.ReportStartupError(platform, e.Message);
    return StartupExit.Configuration;
}
builder.AddServiceHosting(platform);

// ---- Configuration -------------------------------------------------------------------------
builder.Services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
// Pages read IOptions<AppOptions>; serve it with the DB display name (App.DisplayName) applied.
builder.Services.AddSingleton<IOptions<AppOptions>, DynamicAppOptions>();

// App:DataDir, else per platform: the services' state folders, ./data otherwise.
var dataDir = Path.GetFullPath(
    config["App:DataDir"] is { Length: > 0 } configuredDataDir ? configuredDataDir : platform.DefaultDataDir(builder.Environment.ContentRootPath),
    builder.Environment.ContentRootPath);

// ---- Data (DESIGN.md §28: provider and connection string from the environment only) ----------
DatabaseSetup database;
try
{
    database = DatabaseSetup.FromConfiguration(config, dataDir);
}
catch (DatabaseConfigException e)
{
    ServiceHosting.ReportStartupError(platform, e.Message);
    return StartupExit.Configuration;
}
if (command is not null)
{
    return await CopyToPostgresCommand.RunAsync(command, database, dataDir, Console.Out, Console.Error);
}
DataDirectory.Prepare(dataDir);
var adminKeyFile = ServiceHosting.PrepareAdminKeyFile(platform, dataDir);

builder.Services.AddSingleton(database);
// Factory (singleton) + scoped context: Identity uses the scoped one; poll services create a
// short-lived context per operation, which is what long-lived Blazor circuits need. On PostgreSQL the factory hands out
// PostgresDbContext, whose migration set is the PostgreSQL one.
builder.Services.AddDbContextFactory<ApplicationDbContext, AppDbContextFactory>(database.Apply);
if (EF.IsDesignTime)
{
    // dotnet ef … --context PostgresDbContext: the PostgreSQL migration set's model, no server needed.
    builder.Services.AddScoped(_ => PostgresDbContext.ForModel());
}
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
// Npgsql's own log lines can name the server; EF and the app log database failures (redacted, DatabaseErrors).
builder.Logging.AddFilter("Npgsql", LogLevel.None);

// Keys must survive restarts or every cookie (auth, antiforgery, voter cookies) dies with them. DPAPI on Windows (§29).
builder.Services.AddDataProtection().ConfigureKeyRing(DataDirectory.KeysPath(dataDir));

// ---- Blazor ---------------------------------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // The editor's Text-mode text and a restored browser draft (wwwroot/js/draft.js) arrive as one message each: sized
    // from the admin limit ceilings (pentest AV-09), instead of SignalR's 32 kB default or the 1 MB used before.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = LimitSettings.MaxCircuitMessageBytes);

// ---- Identity -------------------------------------------------------------------------------
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Self-registered accounts sign in only once verified (AppSignInManager.AwaitsVerification); admin-created ones
        // may sign in unverified. A verified email is required to create polls and to recover a password.
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // passkeys

        // Length over composition (NIST SP 800-63B).
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        // Pending and deleted accounts have no email; OptionalEmailValidator enforces "valid and
        // unique when set" instead.
        options.User.RequireUniqueEmail = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager<AppSignInManager>()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
    .AddUserValidator<OptionalEmailValidator>()
    .AddDefaultTokenProviders();
// Passkey sign-in never names the account (pentest R2-ACC-3: no allowCredentials oracle), so passkeys must be
// discoverable.
builder.Services.Configure<IdentityPasskeyOptions>(o => o.ResidentKeyRequirement = "required");
// Authenticator keys and recovery codes encrypted with the key ring (pentest AV-12); replaces the plain EF store.
builder.Services.AddScoped<IUserStore<ApplicationUser>, ProtectedTokenUserStore>();

// "Log out everywhere", password resets and admin locks bump the security stamp; check it often.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
// Server-side sessions (pentest R3-DEP-2): the cookie holds a random key, the ticket is in AuthSessions and its stamp is
// checked on every request, so a copy of the key ring alone cannot forge a session.
builder.Services.AddSingleton<AuthSessionStore>();
builder.Services.AddSingleton<AuthSessionCookieSetup>();
builder.Services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>>(sp => sp.GetRequiredService<AuthSessionCookieSetup>());
builder.Services.AddSingleton<IPostConfigureOptions<Microsoft.AspNetCore.Authentication.AuthenticationOptions>>(sp => sp.GetRequiredService<AuthSessionCookieSetup>());
builder.Services.AddTransient<SessionCookieHandler>();

// PBKDF2-HMAC-SHA512; existing hashes are upgraded on next successful login.
builder.Services.Configure<PasswordHasherOptions>(o => o.IterationCount = 600_000);

// Auth cookie is Secure-only outside Development: the app must be reached over HTTPS (proxy or
// cert). Security:AllowInsecureCookies=true is an escape hatch for plain-HTTP LAN testing only.
var authCookieSecurePolicy = isDev || config.GetValue<bool>("Security:AllowInsecureCookies")
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = authCookieSecurePolicy;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
});
foreach (var scheme in new[] { IdentityConstants.TwoFactorUserIdScheme, IdentityConstants.TwoFactorRememberMeScheme, IdentityConstants.ExternalScheme })
{
    builder.Services.Configure<CookieAuthenticationOptions>(scheme, o => o.Cookie.SecurePolicy = authCookieSecurePolicy);
}
// SameAsRequest, not Always: with Always the antiforgery system throws on every plain-HTTP page
// instead of just rendering it. Secure on HTTPS either way.
builder.Services.AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);

// Account gate (DESIGN.md §15): NotGated is part of every policy. Default = [Authorize] pages and
// RequireAuthorization() endpoints; fallback = everything without authorization metadata. Pages a
// gated user must reach use [AllowAnonymous]; anonymous interactive pages carry NotGated explicitly.
builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().AddRequirements(NotGatedRequirement.Instance).Build())
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .AddRequirements(NotGatedRequirement.Instance).Build())
    .AddPolicy(AppPolicies.NotGated, p => p.AddRequirements(NotGatedRequirement.Instance))
    .AddPolicy(AppPolicies.Admin, p => p.RequireAuthenticatedUser().RequireRole(AppRoles.Admin).AddRequirements(NotGatedRequirement.Instance));
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, NotGatedRequirement>(_ => NotGatedRequirement.Instance);
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, GateAuthorizationResultHandler>();

builder.Services.AddSingleton<DummyPasswordHash>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SetupKey>();
builder.Services.AddSingleton<RecoveryKey>();

// ---- Abuse handling, status (DESIGN.md §14.7, §14.8); all in memory ---------------------------
builder.Services.Configure<AbuseOptions>(config.GetSection(AbuseOptions.Section));
builder.Services.AddSingleton<AbuseTracker>();
builder.Services.AddSingleton<BlockedRangeService>(); // persistent CIDR blocks (DB, cached), DESIGN.md §17
builder.Services.AddSingleton<FormGuard>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClientInfo>();
builder.Services.AddSingleton<CircuitCounter>();
builder.Services.AddSingleton<CircuitLimiter>(); // open circuits per client key (pentest AV-09)
builder.Services.AddScoped<CircuitHandler, AppCircuitHandler>();
builder.Services.AddSingleton<AppMetrics>();
builder.Services.AddSingleton<RateCounters>();
builder.Services.AddSingleton<LogRing>();
builder.Services.AddSingleton<ILoggerProvider, LogRingProvider>();
builder.Services.AddSingleton(new AppStartInfo(DateTimeOffset.UtcNow, dataDir));
builder.Services.AddHttpClient(nameof(HttpCaptchaVerifier));
builder.Services.AddSingleton<ICaptchaVerifier, HttpCaptchaVerifier>();

// ---- Settings, mail, accounts -----------------------------------------------------------------
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<BrandingService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<IMailQueue, MailQueue>();
builder.Services.AddSingleton<IMailTransport, SmtpMailTransport>();
builder.Services.AddSingleton<MailSender>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailSender>());
builder.Services.AddScoped<AccountService>();

// ---- Polls ----------------------------------------------------------------------------------
builder.Services.Configure<PollCookieOptions>(o => o.SecurePolicy = authCookieSecurePolicy);
builder.Services.AddSingleton<PollCookies>();
builder.Services.AddSingleton(sp => new VoterKeys(sp.GetRequiredService<IDataProtectionProvider>(), Path.Combine(dataDir, "keys", "voterkey")));
// Email addresses at rest (DESIGN.md §30): encrypted with the key ring, looked up by a keyed hash (secret in keys/emailkey).
builder.Services.AddSingleton(sp => new EmailProtection(sp.GetRequiredService<IDataProtectionProvider>(),
    Path.Combine(DataDirectory.KeysPath(dataDir), "emailkey"), sp.GetRequiredService<ILogger<EmailProtection>>()));
builder.Services.AddScoped<PollAccessService>();
builder.Services.AddScoped<PollService>();
builder.Services.AddScoped<BallotService>();
builder.Services.AddSingleton<VoteFormIds>(); // single-use vote form ids (DESIGN.md §21.1), memory only
builder.Services.AddSingleton<Telrang.Web.Features.Showcase.ShowcaseCache>(); // the home page examples, rendered once a day (DESIGN.md §31)
builder.Services.AddSingleton<ResultsCache>(); // computed results per poll, dropped on change (DESIGN.md §31)
builder.Services.AddScoped<ResultsService>();
builder.Services.AddSingleton<PollSweepService>(); // results-ready mails + retention, one 5-minute timer
builder.Services.AddHostedService(sp => sp.GetRequiredService<PollSweepService>());
builder.Services.AddScoped<CsvExportService>();
builder.Services.AddScoped<AdminBootstrapper>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<StatusService>();

// ---- Live results for the owner (DESIGN.md §21.2); memory only ---------------------------------
builder.Services.AddSingleton<PollChangeNotifier>();
builder.Services.AddSingleton<ChangeLogLimiter>(); // saves that add to a change log, per poll (DESIGN.md §31)
builder.Services.AddSingleton<HeavyGetLimiter>(); // results page and CSVs at once, per client and in all (DESIGN.md §31)
builder.Services.AddSingleton<LiveStreamLimiter>(); // open streams per client, per account and in all

// ---- Edge: body limits, proxy, rate limits, health ---------------------------------------------
// Small bodies and forms for every request (DESIGN.md §31); FormLimitMiddleware gives /_blazor its own limit and enforces
// both on servers other than Kestrel.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = RequestLimits.MaxBodyBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(RequestLimits.Apply);
builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));
builder.Services.AddAppForwardedHeaders(config);
builder.Services.AddAppRateLimiting(config);
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddDatabaseErrorRedaction(); // last: wraps the logger factory (DESIGN.md §28)

var app = builder.Build();

if (operatorFile is not null)
{
    app.Logger.LogInformation("Configuration file: {Path}", operatorFile.Path);
    if (operatorFile.PermissionWarning is { } fileProblem) app.Logger.LogWarning("{Problem}", fileProblem);
}
if (FilePermissions.DataDirProblem(dataDir) is { } dataDirProblem) app.Logger.LogWarning("{Problem}", dataDirProblem);
// The other files that hold secrets (pentest R3-DEP-10): the connection string file and Kestrel's certificates.
foreach (var (secretFile, what) in FilePermissions.SecretFiles(config))
{
    if (FilePermissions.SecretFileProblem(secretFile, what) is { } secretProblem) app.Logger.LogWarning("{Problem}", secretProblem);
}

// ---- Startup: migrate + seed ----------------------------------------------------------------
AdminBootstrapper.Issued issuedKeys;
await using (var scope = app.Services.CreateAsyncScope())
{
    try
    {
        var startDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await DatabaseSetup.MigrateAsync(startDb);
        await DbWrites.EnsureWriterLockAsync(startDb);
        await EmailUpgrade.RunAsync(startDb, scope.ServiceProvider.GetRequiredService<EmailProtection>(), app.Logger);
        if (await database.PoolProblemAsync(startDb) is { } poolProblem) app.Logger.LogWarning("{Problem}", poolProblem);
    }
    catch (Exception e) when (DatabaseErrors.IsDatabaseError(e))
    {
        // Unreachable server, wrong password, missing database: the type and SQL state only (DESIGN.md §28).
        app.Logger.LogCritical("Telrang cannot start: the {Provider} database cannot be opened or migrated ({Error}).",
            database.DisplayName, DatabaseErrors.Describe(e));
        return StartupExit.Database;
    }
    issuedKeys = await scope.ServiceProvider.GetRequiredService<AdminBootstrapper>().SeedAsync();
    scope.ServiceProvider.GetRequiredService<DummyPasswordHash>(); // hash once now, not on the first login
    _ = scope.ServiceProvider.GetRequiredService<SettingsService>().Current; // warm the settings cache
    scope.ServiceProvider.GetRequiredService<RateCounters>(); // start listening before the first request
    // The way out of a lock-out by a blocked range (pentest R2-ACC-7): Abuse__ClearBlockedRangesOnStart=true.
    if (config.GetValue("Abuse:ClearBlockedRangesOnStart", false))
    {
        var removed = await scope.ServiceProvider.GetRequiredService<BlockedRangeService>().ClearAllAsync();
        app.Logger.LogWarning("Abuse:ClearBlockedRangesOnStart is set: removed all {Count} blocked ranges. Remove the setting.", removed);
    }
    scope.ServiceProvider.GetRequiredService<BlockedRangeService>().IsBlocked(System.Net.IPAddress.Loopback); // load the ranges
}

// ---- Pipeline -------------------------------------------------------------------------------
// Explicit order (pentest AV-05): without explicit UseRouting/UseAuthentication/UseAuthorization, WebApplication puts
// them first, so challenges were answered before X-Forwarded-Proto and without the security headers.
app.UseAppForwardedHeaders(); // X-Forwarded-* from trusted TCP proxies and from Unix socket peers (pentest R3-DEP-3, R3-DEP-7)
app.UseSecurityHeaders();
app.UseMiddleware<PollNoStoreMiddleware>(); // /p/... responses carry ballots and names (DESIGN.md §21.1)
app.UseMiddleware<PublicHostFilterMiddleware>(); // other Host headers are refused once App.PublicUrl is set
app.UseMiddleware<BlazorOriginMiddleware>(); // /_blazor from another site's page is refused (pentest R2 Info a)

if (isDev)
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts(); // only emitted on HTTPS requests
}

if (ReverseProxy.HttpsEndpointConfigured(config))
{
    // Keep /healthz reachable over plain HTTP for container health probes.
    app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/healthz"), b => b.UseHttpsRedirection());
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRouting();
app.UseMiddleware<AbuseBlockMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<HeavyGetMiddleware>(); // results page and CSVs: per-client and total concurrency (DESIGN.md §31)
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<FormLimitMiddleware>(); // a body or form over the limits: 413 / 400 page, not a 500 (DESIGN.md §21.1, §31)
app.UseAntiforgery();
app.UseMiddleware<AntiforgeryFailureMiddleware>();
app.UseMiddleware<FormGuardMiddleware>();

app.MapHealthChecks("/healthz").DisableHttpMetrics().AllowAnonymous().ExemptFromAbuseBlock();
app.MapStaticAssets().AllowAnonymous().ExemptFromAbuseBlock(); // the fallback policy would otherwise gate CSS/JS on the enrolment page
app.MapBranding(); // /branding/theme.css and the images, anonymous like static files
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();
app.MapPollExports();
app.MapAccountEndpoints();
app.MapAdminBackup();

// Live results for the owner (DESIGN.md §21.2): server-sent events, owner only.
app.MapLiveResults();

// The setup/recovery key is logged once the server knows its addresses, so the line names the URL to open.
app.Lifetime.ApplicationStarted.Register(() => ReverseProxy.OpenUnixSockets(app.Urls, app.Logger));
app.Lifetime.ApplicationStarted.Register(() =>
    AdminBootstrapper.LogIssued(issuedKeys, app.Logger, app.Services.GetRequiredService<SettingsService>().Current.PublicUrl, app.Urls, adminKeyFile));

await app.RunAsync();
return 0;

public partial class Program;
