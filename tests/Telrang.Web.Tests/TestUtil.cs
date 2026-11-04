using System.Net;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Telrang.Web.Tests;

/// <summary>Shared helpers for the integration tests (service-level poll setup, cookies, fake peers).</summary>
internal static class TestUtil
{
    public const string Password = "correct horse battery staple";

    /// <summary>A timestamp as the database gives it back: PostgreSQL keeps microseconds (SQLite every tick), DESIGN.md §28.</summary>
    public static DateTimeOffset Stored(DateTimeOffset value) =>
        AppFactory.OnPostgres ? value.AddTicks(-(value.UtcTicks % 10)) : value;

    // ---- poll drafts --------------------------------------------------------------------------
    // Polls are built as PollDraft (the editor's model, DESIGN.md §20.1). The fluent helpers below (DraftBuilder)
    // return modified copies, so a shared base draft is never changed by a test.

    /// <summary>"Trip": ranked, Borda, 1 ban, Paris / Rome / Berlin, results for the owner, anyone with the link.</summary>
    public static PollDraft RankedDraft(ResultsVisibility results = ResultsVisibility.Creator, VoterMode voters = VoterMode.Public, string title = "Trip") =>
        Ranked(title, "Paris", "Rome", "Berlin").Describe("Where shall we go?").Results(results).Voters(voters).Q(q => q.Bans = 1);

    /// <summary>"Budget": points, budget 10, Paris / Rome / Berlin.</summary>
    public static PollDraft PointsDraft(ResultsVisibility results = ResultsVisibility.Creator, VoterMode voters = VoterMode.Public, int budget = 10) =>
        Points("Budget", budget, "Paris", "Rome", "Berlin").Describe("Spend wisely.").Results(results).Voters(voters);

    /// <summary>A ranked poll with these option labels (default A, B).</summary>
    public static PollDraft Ranked(string title, params string[] options) => new()
    {
        Title = title,
        Questions = [new QuestionDraft { Type = PollType.Ranked, Options = Labels(options.Length == 0 ? ["A", "B"] : options) }],
    };

    /// <summary>A points poll with this budget and option labels (default A, B).</summary>
    public static PollDraft Points(string title, int budget, params string[] options) => new()
    {
        Title = title,
        Questions = [new QuestionDraft { Type = PollType.Points, Budget = budget, Options = Labels(options.Length == 0 ? ["A", "B"] : options) }],
    };

    /// <summary>A dates poll; each line is a Text-mode date line (<c>2026-11-03</c>, <c>2026-11-05 19:00</c>, …).</summary>
    public static PollDraft Dates(string title, params string[] lines) => new()
    {
        Title = title,
        Questions = [new QuestionDraft { Type = PollType.Dates, Options = ParseOptions(PollType.Dates, string.Join("\n", lines)) }],
    };

    private static List<OptionDraft> Labels(IEnumerable<string> labels) => labels.Select(l => new OptionDraft { Label = l }).ToList();

    /// <summary>Options from the editor's Text mode (one per line, indented lines are the details).</summary>
    public static List<OptionDraft> ParseOptions(PollType type, string text)
    {
        var parsed = OptionListParser.Parse(text, type, PollLimits.Default);
        Assert.True(parsed.Success, string.Join("; ", parsed.Errors.Select(e => $"{e.Line}: {e.Message}")));
        return parsed.Options.ToList();
    }

    /// <summary>The validated definition of a draft (default limits), for tests that need one without a database.</summary>
    public static PollDefinition Definition(PollDraft draft)
    {
        var r = PollDraftValidator.Validate(draft, PollLimits.Default);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return r.Definition!;
    }

    public static async Task<string> CreateUserAsync(AppFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        // Verified: poll creation requires it (accounts made through the UI verify by mail).
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>Marks an account's email verified (for accounts created through the register form).</summary>
    public static async Task<string> ConfirmEmailAsync(AppFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        user.EmailConfirmed = true;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
        return user.Id;
    }

    /// <summary>
    /// Configures mail settings so the outbox accepts messages (delivery goes to the fake transport). Mail needs a Public
    /// URL (pentest AV-01): <c>https://localhost</c> unless the test set one already.
    /// </summary>
    public static Task ConfigureMailAsync(AppFactory factory, int hourlyCap = 100, int cooldownMinutes = 15, int dailyMax = 3) =>
        factory.Settings.SetAsync(new Dictionary<string, string?>
        {
            [Telrang.Web.Features.Settings.SettingKeys.PublicUrl] = factory.Settings.Current.PublicUrl ?? "https://localhost",
            [Telrang.Web.Features.Settings.SettingKeys.MailHost] = "smtp.example.test",
            [Telrang.Web.Features.Settings.SettingKeys.MailFromAddress] = "vote@example.test",
            [Telrang.Web.Features.Settings.SettingKeys.MailHourlyCap] = hourlyCap.ToString(),
            [Telrang.Web.Features.Settings.SettingKeys.MailCooldownMinutes] = cooldownMinutes.ToString(),
            [Telrang.Web.Features.Settings.SettingKeys.MailDailyMax] = dailyMax.ToString(),
        }, null);

    public static async Task<T> WithScope<T>(AppFactory factory, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public static async Task<string> CreatePollAsync(AppFactory factory, string ownerId, PollDraft draft)
    {
        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(ownerId, draft));
        Assert.True(result.Succeeded, result.Message + string.Join(", ", result.Errors));
        return result.EncodedId!;
    }

    public static Task<PollSaveResult> UpdatePollAsync(AppFactory factory, string userId, string id, PollDraft draft) =>
        WithScope(factory, sp => sp.GetRequiredService<PollService>().UpdatePollAsync(userId, id, draft));

    public static VisitorContext VisitorFrom(AppFactory factory, CookieContainer jar, string id, string? userId = null)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        var cookies = jar.GetCookies(new Uri($"https://localhost/p/{id}"));
        return factory.Services.GetRequiredService<PollCookies>().Read(name => cookies[name]?.Value, pollId, userId);
    }

    public static Task<SubmitResult> SubmitRankedAsync(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot, string? name = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, name));

    public static Task<SubmitResult> SubmitPointsAsync(AppFactory factory, string id, VisitorContext visitor, Dictionary<string, int> points, string? name = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitPointsAsync(id, visitor, new PointsBallot(points), name));

    public static async Task<List<Ballot>> BallotsAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Ballots.AsNoTracking().Where(b => b.PollId == pollId).ToListAsync());
    }

    /// <summary>
    /// GET that follows same-site redirects like a browser (the vote page's one-time cookie check
    /// redirects a first visit to <c>?ck=1</c> and back). Returns the final response.
    /// </summary>
    public static async Task<HttpResponseMessage> GetPageAsync(HttpClient client, string path, int maxRedirects = 3)
    {
        var response = await client.GetAsync(path);
        for (var i = 0; i < maxRedirects && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308; i++)
        {
            response = await client.GetAsync(response.Headers.Location!);
        }
        return response;
    }

    /// <summary>Signs in through the real login form; returns the browser (cookie jar holds the auth cookie).</summary>
    public static async Task<HttpClient> LoggedInBrowserAsync(AppFactory factory, string email)
    {
        var browser = factory.CreateBrowser();
        var login = await AppFactory.LoginAsync(browser, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return browser;
    }

    // ---- encrypted addresses (DESIGN.md §30) ----------------------------------------------------------

    /// <summary>The address (or admin log note) behind a stored value: addresses are stored encrypted.</summary>
    public static string? Reveal(AppFactory factory, string? stored) => factory.Services.GetRequiredService<EmailProtection>().Reveal(stored);

    /// <summary>The lookup hash stored for an address (<c>NormalizedEmail</c>, <c>OutboxMessage.ToHash</c>).</summary>
    public static string EmailHash(AppFactory factory, string address) => factory.Services.GetRequiredService<EmailProtection>().Hash(address);

    /// <summary>Outbox rows as the tests compare them: recipient and bodies decrypted (they are stored encrypted).</summary>
    public static List<OutboxMessage> Revealed(AppFactory factory, IEnumerable<OutboxMessage> rows)
    {
        var emails = factory.Services.GetRequiredService<EmailProtection>();
        return rows.Select(m =>
        {
            m.To = emails.Reveal(m.To)!;
            m.TextBody = emails.RevealBody(m.TextBody);
            m.HtmlBody = emails.RevealBody(m.HtmlBody);
            return m;
        }).ToList();
    }

    /// <summary>An account row with its addresses decrypted (for asserts on Email and UserName).</summary>
    public static ApplicationUser Revealed(AppFactory factory, ApplicationUser user)
    {
        user.Email = Reveal(factory, user.Email);
        user.UserName = Reveal(factory, user.UserName);
        return user;
    }

    /// <summary>
    /// Signs in with the password and a code of the account's authenticator key (2FA on). A change of the security stamp
    /// made beside the browser (2FA turned on in a test scope) ends the account's sessions at once (server-side sessions,
    /// pentest R3-DEP-2), so tests sign in after it.
    /// </summary>
    public static async Task<HttpClient> LoggedInWithTotpAsync(AppFactory factory, string email)
    {
        var key = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.GetAuthenticatorKeyAsync((await users.FindByEmailAsync(email))!))!;
        });
        var browser = factory.CreateBrowser();
        var login = await AppFactory.LoginAsync(browser, email, Password);
        Assert.Contains("LoginWith2fa", login.Headers.Location!.ToString());
        var done = await AppFactory.SubmitFormAsync(browser, login.Headers.Location!.ToString(), "login-with-2fa",
            new Dictionary<string, string> { ["Input.TwoFactorCode"] = Phase5aTests.Totp(key) });
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        return browser;
    }

    // ---- fake client address ----------------------------------------------------------------

    public const string PeerHeader = "X-Test-Peer";

    /// <summary>
    /// TestServer has no real connection address. This startup filter runs before Program's pipeline
    /// (so before UseForwardedHeaders) and sets RemoteIpAddress from the <see cref="PeerHeader"/> header.
    /// </summary>
    public static void AddFakePeer(IServiceCollection services) => services.AddSingleton<IStartupFilter, FakePeerStartupFilter>();

    private sealed class FakePeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers.TryGetValue(PeerHeader, out var peer)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer!);
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    /// <summary>
    /// Inserts an account row into a database migrated to an older schema, where the current model's
    /// <c>ApplicationUser</c> has columns the table does not have yet. Fills every NOT NULL column without a default
    /// (0, or a fixed date for *At columns); <paramref name="values"/> sets others by column name.
    /// </summary>
    public static async Task InsertLegacyUserAsync(Microsoft.EntityFrameworkCore.DbContext db, string id, string email,
        IReadOnlyDictionary<string, object?>? values = null)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        var row = new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserName"] = email, ["NormalizedUserName"] = email.ToUpperInvariant(),
            ["Email"] = email, ["NormalizedEmail"] = email.ToUpperInvariant(),
        };
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "SELECT name, \"notnull\", dflt_value FROM pragma_table_info('AspNetUsers')";
            using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (row.ContainsKey(name) || reader.GetInt64(1) == 0 || !reader.IsDBNull(2)) continue;
                row[name] = name.EndsWith("At", StringComparison.Ordinal) ? "2026-01-01 00:00:00+00:00" : 0;
            }
        }
        foreach (var (k, v) in values ?? new Dictionary<string, object?>()) row[k] = v;
        using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO \"AspNetUsers\" ({string.Join(", ", row.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(", ", row.Keys.Select((_, i) => $"$p{i}"))})";
        var n = 0;
        foreach (var v in row.Values)
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = $"$p{n++}";
            parameter.Value = v ?? DBNull.Value;
            insert.Parameters.Add(parameter);
        }
        await insert.ExecuteNonQueryAsync();
    }

    /// <summary>A clock tests can move.</summary>
    public sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>Fluent changes of a <see cref="PollDraft"/>; each returns a modified deep copy.</summary>
internal static class DraftBuilder
{
    public static PollDraft With(this PollDraft d, Action<PollDraft> change)
    {
        var copy = PollJson.DeserializeDraft(PollJson.Serialize(d))!;
        change(copy);
        return copy;
    }

    /// <summary>Changes the (single) question.</summary>
    public static PollDraft Q(this PollDraft d, Action<QuestionDraft> change) => d.With(x => change(x.Questions[0]));

    public static PollDraft Titled(this PollDraft d, string title) => d.With(x => x.Title = title);
    public static PollDraft Describe(this PollDraft d, string description) => d.With(x => x.Description = description);
    public static PollDraft Voters(this PollDraft d, VoterMode voters) => d.With(x => x.VoterMode = voters);
    public static PollDraft Results(this PollDraft d, ResultsVisibility results) => d.With(x => x.ResultsVisibility = results);
    public static PollDraft Responses(this PollDraft d, ResponsePolicy responses) => d.With(x => x.Responses = responses);
    public static PollDraft Many(this PollDraft d) => d.Responses(ResponsePolicy.Many);
    public static PollDraft Names(this PollDraft d, NamesMode names) => d.With(x => x.Names = names);
    public static PollDraft Closes(this PollDraft d, DateOnly? day) => d.With(x => x.ClosesOn = day);
    public static PollDraft Closes(this PollDraft d, string isoDay) => d.Closes(DateOnly.Parse(isoDay, System.Globalization.CultureInfo.InvariantCulture));
    public static PollDraft Zone(this PollDraft d, string? timeZone) => d.With(x => x.TimeZone = timeZone);

    /// <summary>Replaces the options with labels (ranked/points) or date lines (dates).</summary>
    public static PollDraft Options(this PollDraft d, params string[] lines) =>
        d.OptionsText(string.Join("\n", lines));

    /// <summary>Replaces the options from Text-mode text (indented lines are details).</summary>
    public static PollDraft OptionsText(this PollDraft d, string text) =>
        d.Q(q => q.Options = TestUtil.ParseOptions(q.Type, text));
}
