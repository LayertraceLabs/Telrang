using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Reports;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §34.8: the report form (static, guarded, rate limited, no IP or identity stored, text and contact encrypted,
/// capped), the admin's Reports tab and its actions, the settings, and the admins' one mail.
/// </summary>
public class ReportTests
{
    private static Guid Raw(string id) => PollId.TryDecode(id, out var g) ? g : throw new ArgumentException(id);

    private static Dictionary<string, string> Fields(string category = "Spam", string text = "", string contact = "") => new()
    {
        [ReportPage.CategoryField] = category,
        [ReportPage.TextField] = text,
        [ReportPage.ContactField] = contact,
    };

    private static Task<HttpResponseMessage> PostReportAsync(HttpClient client, string id, Dictionary<string, string> fields) =>
        AppFactory.SubmitFormAsync(client, $"/p/{id}/report", ReportPage.FormName, fields);

    private static Task<List<PollReport>> ReportsAsync(AppFactory factory) =>
        WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().PollReports.AsNoTracking().ToListAsync());

    private static Task<ReportResult> SubmitAsync(AppFactory factory, string id, ReportCategory? category = ReportCategory.Spam,
        string? text = null, string? contact = null, string? userId = null) =>
        WithScope(factory, sp => sp.GetRequiredService<ReportService>().SubmitAsync(id, userId, category, text, contact));

    private static Task<T> AdminAsync<T>(AppFactory factory, Func<AdminService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<AdminService>()));

    [Fact]
    public async Task An_anonymous_visitor_reports_a_poll_without_javascript_and_nothing_identifies_them()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(ResultsVisibility.Open).Titled("Bake sale"));
        var look = ThemeLook.Create("#5a2a5f", "#d9a62e", "#2a6f2b", "#e0457b", "serif", 12).Look!;
        await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.Where(p => p.Id == Raw(id))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.LookJson, look.ToJson())));

        var browser = factory.CreateBrowser();
        var vote = await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"p/{id}/report\" data-enhance-nav=\"false\">Report</a>", vote);
        Assert.Contains($"href=\"p/{id}/report\"", await (await GetPageAsync(browser, $"/p/{id}/results")).Content.ReadAsStringAsync());

        var form = await browser.GetAsync($"/p/{id}/report");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Equal("no-store", form.Headers.CacheControl?.ToString());
        var html = await form.Content.ReadAsStringAsync();
        Assert.Contains("Bake sale", html);
        Assert.Contains("theme.css?admin=1", html); // no custom CSS ...
        Assert.DoesNotContain("look.css", html); // ... and not the poll's look
        Assert.Contains($"name=\"{FormGuard.HoneypotField}\"", html);
        Assert.Contains($"<form method=\"post\" action=\"p/{id}/report\"", html); // a plain form
        foreach (var (category, label) in ReportService.Categories) Assert.Contains($"value=\"{category}\"", html);

        var sent = await PostReportAsync(browser, id, Fields("IllegalContent", "Line one\r\nline <b>two</b>", " reporter@example.com "));
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        Assert.EndsWith($"/p/{id}/report?sent=1", sent.Headers.Location!.ToString());
        Assert.Contains("Report sent.", await (await browser.GetAsync(sent.Headers.Location)).Content.ReadAsStringAsync());

        var report = Assert.Single(await ReportsAsync(factory));
        Assert.Equal(Raw(id), report.PollId);
        Assert.Equal(ReportCategory.IllegalContent, report.Category);
        Assert.Equal(ReportState.Open, report.State);
        Assert.StartsWith(EmailProtection.Prefix, report.TextCipher);
        Assert.StartsWith(EmailProtection.Prefix, report.ContactCipher);
        Assert.Equal("Line one\nline <b>two</b>", Reveal(factory, report.TextCipher));
        Assert.Equal("reporter@example.com", Reveal(factory, report.ContactCipher));
        // The table has no column that could hold the reporter's address, browser, voter identity or account.
        var columns = await WithScope(factory, sp => Task.FromResult(sp.GetRequiredService<ApplicationDbContext>().Model
            .FindEntityType(typeof(PollReport))!.GetProperties().Select(p => p.Name).Order().ToList()));
        Assert.Equal(["Category", "ContactCipher", "CreatedAt", "HandledAt", "HandledLogId", "Id", "PollId", "State", "TextCipher"], columns);
    }

    [Fact]
    public async Task The_form_says_what_to_change_and_stores_nothing_invalid()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["RateLimiting:ReportPermitPerMinute"] = "100" });
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = factory.CreateBrowser();

        var none = await PostReportAsync(browser, id, Fields(category: "", text: "kept as typed"));
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        var html = await none.Content.ReadAsStringAsync();
        Assert.Contains(ReportService.CategoryMessage, html);
        Assert.Contains("kept as typed", html);
        Assert.Contains(ReportService.CategoryMessage, await (await PostReportAsync(browser, id, Fields(category: "4"))).Content.ReadAsStringAsync());
        Assert.Contains(ReportService.ContactMessage, await (await PostReportAsync(browser, id, Fields(contact: "not an address"))).Content.ReadAsStringAsync());
        Assert.Contains(WebUtility.HtmlEncode(ReportService.TextMessage),
            await (await PostReportAsync(browser, id, Fields(text: new string('x', PollReport.MaxTextLength + 1)))).Content.ReadAsStringAsync());
        // 1000 characters with line breaks sent as CRLF fit.
        var lines = string.Join("\r\n", Enumerable.Repeat(new string('y', 99), 10));
        Assert.True(lines.Length > PollReport.MaxTextLength);
        Assert.Equal(HttpStatusCode.Redirect, (await PostReportAsync(browser, id, Fields(text: lines))).StatusCode);
        Assert.Single(await ReportsAsync(factory));

        // Unknown or undecodable ids are 404, as on the poll's other pages.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/p/AAAAAAAAAAAAAAAAAAAAAA/report")).StatusCode);
        Assert.Equal(ReportStatus.NotFound, (await SubmitAsync(factory, "AAAAAAAAAAAAAAAAAAAAAA")).Status);
        Assert.Equal(ReportStatus.NotFound, (await SubmitAsync(factory, "../../x")).Status);
    }

    [Fact]
    public async Task The_form_is_guarded_and_rate_limited_on_its_own()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["RateLimiting:ReportPermitPerMinute"] = "2" });
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        Assert.True(FormGuard.IsGuarded(new PathString($"/p/{id}/report")));
        Assert.False(FormGuard.IsGuarded(new PathString($"/p/{id}")));
        Assert.True(FormGuard.IsGuarded(new PathString($"/P/{id}/Report/"))); // routing ignores case, so does the guard
        Assert.True(ThemeService.UsesAdminVariant(new PathString($"/p/{id}/report")));
        Assert.False(ThemeService.UsesAdminVariant(new PathString($"/p/{id}")));

        var bot = factory.CreateBrowser();
        var honeypot = Fields();
        honeypot[FormGuard.HoneypotField] = "https://spam.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await PostReportAsync(bot, id, honeypot)).StatusCode);

        var browser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await PostReportAsync(browser, id, Fields())).StatusCode);
        var limited = await PostReportAsync(browser, id, Fields());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Single(await ReportsAsync(factory));
    }

    [Fact]
    public async Task Open_reports_are_capped_per_poll_and_in_all_and_the_visitor_cannot_tell()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var other = await CreatePollAsync(factory, owner, RankedDraft().Titled("Other"));
        for (var i = 0; i < ReportService.MaxOpenPerPoll + 3; i++) Assert.Equal(ReportStatus.Sent, (await SubmitAsync(factory, id, text: "flood " + i)).Status);
        Assert.Equal(ReportService.MaxOpenPerPoll, (await ReportsAsync(factory)).Count);

        // In all: filled up with rows of the other poll. A third poll's first report is still stored (the cap in all cannot
        // hide a poll), its second is accepted and not stored.
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            for (var i = ReportService.MaxOpenPerPoll; i < ReportService.MaxOpen; i++)
                db.PollReports.Add(new PollReport { Id = Guid.NewGuid(), PollId = Raw(other), CreatedAt = DateTimeOffset.UtcNow, Category = ReportCategory.Spam });
            return await db.SaveChangesAsync();
        });
        var third = await CreatePollAsync(factory, owner, RankedDraft().Titled("Third"));
        Assert.Equal(ReportStatus.Sent, (await SubmitAsync(factory, third)).Status);
        Assert.Equal(ReportService.MaxOpen + 1, (await ReportsAsync(factory)).Count);
        Assert.Equal(ReportStatus.Sent, (await SubmitAsync(factory, third)).Status);
        Assert.Equal(ReportService.MaxOpen + 1, (await ReportsAsync(factory)).Count);
    }

    [Fact]
    public async Task Reports_off_hides_the_link_and_the_form_answers_404()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = factory.CreateBrowser();
        var token = AppFactory.ScrapeAntiforgeryToken(await (await browser.GetAsync($"/p/{id}/report")).Content.ReadAsStringAsync());
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ReportsEnabled] = "false" }, null);

        Assert.DoesNotContain("/report\"", await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/p/{id}/report")).StatusCode);
        var post = await browser.PostAsync($"/p/{id}/report", new FormUrlEncodedContent(new Dictionary<string, string>(Fields())
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = ReportPage.FormName,
        }));
        Assert.True(post.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, post.StatusCode.ToString());
        Assert.Equal(ReportStatus.Off, (await SubmitAsync(factory, id)).Status);
        Assert.Empty(await ReportsAsync(factory));
    }

    [Fact]
    public async Task A_disabled_poll_cannot_be_reported_by_visitors_and_its_owner_sees_no_link()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        var id = await CreatePollAsync(factory, owner, RankedDraft());

        var ownerBrowser = await LoggedInBrowserAsync(factory, "owner@example.com");
        Assert.DoesNotContain("/report\"", await (await GetPageAsync(ownerBrowser, $"/p/{id}")).Content.ReadAsStringAsync());

        Assert.True((await AdminAsync(factory, a => a.DisablePollAsync(admin, id))).Ok);
        var visitor = factory.CreateBrowser();
        var gone = await visitor.GetAsync($"/p/{id}/report");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Contains(Telrang.Web.Security.PollAccessService.UnavailableMessage, await gone.Content.ReadAsStringAsync());
        Assert.Equal(ReportStatus.Unavailable, (await SubmitAsync(factory, id)).Status);
        Assert.Empty(await ReportsAsync(factory));
    }

    [Fact]
    public async Task The_admin_sees_open_reports_escaped_dismisses_disables_enables_and_deletes()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        var id = await CreatePollAsync(factory, owner, RankedDraft().Titled("Bake sale"));
        Assert.Equal(ReportStatus.Sent, (await SubmitAsync(factory, id, ReportCategory.Impersonation, "<script>alert(1)</script>\nsecond line", "r@example.com")).Status);
        Assert.Equal(ReportStatus.Sent, (await SubmitAsync(factory, id, ReportCategory.Spam)).Status);

        var group = Assert.Single((await AdminAsync(factory, a => a.ListReportsAsync(admin)))!);
        Assert.Equal(id, group.EncodedId);
        Assert.Equal("owner@example.com", group.OwnerEmail);
        Assert.Equal(2, group.Open.Count);
        Assert.Contains(group.Open, r => r.Text == "<script>alert(1)</script>\nsecond line" && r.Contact == "r@example.com");
        Assert.Equal(2, (await AdminAsync(factory, a => a.GetStatsAsync(admin)))!.OpenReports);

        // The tab: count on its label, the text as escaped plain text, the owner linked to the Users tab.
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var tab = await (await GetPageAsync(browser, "/admin?tab=reports")).Content.ReadAsStringAsync();
        Assert.Contains("title=\"Open reports\">2</span>", tab);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", tab);
        Assert.DoesNotContain("<script>alert(1)", tab);
        Assert.Contains($"href=\"admin?user={owner}\"", tab);
        var users = await (await GetPageAsync(browser, $"/admin?user={owner}")).Content.ReadAsStringAsync();
        Assert.Contains("owner@example.com", users);
        Assert.DoesNotContain(">admin@example.com<", users);

        // A non-admin gets nothing.
        var ownerPrincipal = await WithScope(factory, async sp =>
        {
            var u = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync((await u.FindByIdAsync(owner))!);
        });
        Assert.Null(await AdminAsync(factory, a => a.ListReportsAsync(ownerPrincipal)));
        Assert.False((await AdminAsync(factory, a => a.DismissReportsAsync(ownerPrincipal, id))).Ok);

        // Disable: acted on, still listed (to enable from here); enable: open again; dismiss: closed and erased.
        Assert.True((await AdminAsync(factory, a => a.DisablePollAsync(admin, id))).Ok);
        Assert.All(await ReportsAsync(factory), r => Assert.Equal(ReportState.Acted, r.State));
        var disabled = Assert.Single((await AdminAsync(factory, a => a.ListReportsAsync(admin)))!);
        Assert.True(disabled.IsDisabled);
        Assert.Empty(disabled.Open);
        Assert.Equal(2, disabled.ActedCount);
        Assert.True((await AdminAsync(factory, a => a.EnablePollAsync(admin, id))).Ok);
        Assert.All(await ReportsAsync(factory), r => Assert.Equal(ReportState.Open, r.State));
        Assert.True((await AdminAsync(factory, a => a.DismissReportsAsync(admin, id))).Ok);
        Assert.False((await AdminAsync(factory, a => a.DismissReportsAsync(admin, id))).Ok);
        var dismissed = await ReportsAsync(factory);
        Assert.All(dismissed, r =>
        {
            Assert.Equal(ReportState.Dismissed, r.State);
            Assert.Null(r.TextCipher);
            Assert.Null(r.ContactCipher);
            Assert.NotNull(r.HandledAt);
        });
        var logId = dismissed[0].HandledLogId!.Value;
        var entry = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AdminActionLogs.AsNoTracking().SingleAsync(l => l.Id == logId));
        Assert.Equal("reports.dismiss", entry.Action);
        Assert.Equal("Bake sale: 2 reports", Reveal(factory, entry.Note));
        Assert.Empty((await AdminAsync(factory, a => a.ListReportsAsync(admin)))!);

        // Delete takes the poll's reports with it.
        Assert.Equal(ReportStatus.Sent, (await SubmitAsync(factory, id)).Status);
        Assert.True((await AdminAsync(factory, a => a.DeletePollAsync(admin, id))).Ok);
        Assert.Empty(await ReportsAsync(factory));
    }

    [Fact]
    public async Task The_admins_get_one_mail_for_a_flood_and_it_holds_nothing_of_the_reports()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory, cooldownMinutes: 0, dailyMax: 50);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        await LiveResultsNotifierTests.AdminAsync(factory, "boss@example.com");
        var a = await CreatePollAsync(factory, owner, RankedDraft().Titled("Poll one"));
        var b = await CreatePollAsync(factory, owner, RankedDraft().Titled("Poll two"));
        for (var i = 0; i < 5; i++) await SubmitAsync(factory, a, text: "secret text", contact: "reporter@example.com");
        await SubmitAsync(factory, b, text: "secret text");

        var mails = Revealed(factory, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.Type == MailType.ReportReceived).ToListAsync()));
        var mail = Assert.Single(mails);
        Assert.Equal("boss@example.com", mail.To);
        Assert.Contains("https://localhost/admin?tab=reports", mail.TextBody);
        foreach (var leaked in new[] { "secret", "reporter@", "Poll one", "Poll two", a, b }) Assert.DoesNotContain(leaked, mail.TextBody + mail.Subject);
        Assert.Empty(await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.Type == MailType.PollDisabled).ToListAsync()));
    }

    [Fact]
    public async Task A_signed_in_reporter_is_not_recorded_and_their_data_export_holds_no_report()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var reporter = await CreateUserAsync(factory, "reporter@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "reporter@example.com");
        Assert.Equal(HttpStatusCode.Redirect, (await PostReportAsync(browser, id, Fields("Abuse", "unique-report-text"))).StatusCode);
        var report = Assert.Single(await ReportsAsync(factory));
        Assert.Null(report.ContactCipher);

        var export = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var accounts = sp.GetRequiredService<AccountService>();
            return (Reporter: Encoding.UTF8.GetString(await accounts.ExportAsync((await users.FindByIdAsync(reporter))!)),
                Owner: Encoding.UTF8.GetString(await accounts.ExportAsync((await users.FindByIdAsync(owner))!)));
        });
        Assert.DoesNotContain("unique-report-text", export.Reporter);
        Assert.DoesNotContain("unique-report-text", export.Owner);
    }

    [Fact]
    public async Task Settings_reports_switch_and_contact_line_on_the_legal_page()
    {
        await using var factory = new AppFactory();
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync("/about")).StatusCode);

        Assert.False((await AdminAsync(factory, a => a.SaveSettingsAsync(admin, new Dictionary<string, string?> { [SettingKeys.ReportsEnabled] = "maybe" }, "settings.save"))).Ok);
        var tooLong = await AdminAsync(factory, a => a.SaveSettingsAsync(admin,
            new Dictionary<string, string?> { [SettingKeys.ReportsContact] = new string('x', AppSettingsSnapshot.MaxReportsContactLength + 1) }, "settings.save"));
        Assert.Equal($"Report contact: at most {AppSettingsSnapshot.MaxReportsContactLength} characters.", tooLong.Message);
        Assert.True((await AdminAsync(factory, a => a.SaveSettingsAsync(admin, new Dictionary<string, string?>
        {
            [SettingKeys.ReportsEnabled] = "false",
            [SettingKeys.ReportsContact] = "notices <at> example.org",
        }, "settings.save"))).Ok);
        Assert.False(factory.Settings.Current.ReportsEnabled);

        var about = await factory.CreateBrowser().GetAsync("/about");
        Assert.Equal(HttpStatusCode.OK, about.StatusCode);
        var html = await about.Content.ReadAsStringAsync();
        Assert.Contains("Reports · <bdi>notices &lt;at&gt; example.org</bdi>", html);
        Assert.Contains("href=\"about\"", html); // the footer links the page once the contact line alone makes it exist

        var settings = await (await GetPageAsync(await LoggedInBrowserAsync(factory, "admin@example.com"), "/admin?tab=settings")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"About Reports\"", settings);
        Assert.Contains("aria-label=\"About Report contact\"", settings);
        Assert.Contains($"At most {ReportService.MaxOpenPerPoll} open reports per poll and {ReportService.MaxOpen} in all", settings);
    }
}
