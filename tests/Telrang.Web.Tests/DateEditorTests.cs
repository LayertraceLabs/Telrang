using System.Net;
using System.Text;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §27: option dates from 0001-01-01 to 9999-12-31 through create, vote, results and CSV in zones far from UTC
/// (option dates are wall-clock and never converted, so nothing may overflow), and the editor's calendar as rendered.
/// </summary>
public class DateEditorTests
{
    // Sorted: 0001-01-01, 0001-01-01 00:00, 9999-12-31, 9999-12-31 23:00–23:59, 9999-12-31 23:59.
    private const string FirstDay = "0001-01-01", FirstMidnight = "0001-01-01-00-00", LastDay = "9999-12-31",
        LastSpan = "9999-12-31-23-00-23-59", LastMinute = "9999-12-31-23-59";

    private static PollDraft EdgeDraft(string zone) =>
        Dates("Edges", "9999-12-31 23:59", "0001-01-01 00:00", "9999-12-31", "9999-12-31 23:00-23:59", "0001-01-01")
            .Zone(zone).Results(ResultsVisibility.Open).Names(NamesMode.Optional);

    private static VisitorContext Anon(string key) => new(null, key, null);

    [Theory]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("Pacific/Pago_Pago")]
    [InlineData("UTC")]
    public async Task Dates_at_the_ends_of_the_calendar_create_vote_show_and_export(string zone)
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        // The owner views times in another far zone than the poll's (PollTime.ViewerZone).
        var viewerZone = zone == "Pacific/Kiritimati" ? "Pacific/Pago_Pago" : "Pacific/Kiritimati";
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(owner))!;
            user.TimeZone = viewerZone;
            return (await users.UpdateAsync(user)).Succeeded;
        });
        var id = await CreatePollAsync(factory, owner, EdgeDraft(zone));

        var stored = await WithScope(factory, async sp =>
        {
            var access = await sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(owner));
            return access!.Definition;
        });
        Assert.Equal([FirstDay, FirstMidnight, LastDay, LastSpan, LastMinute], stored.Questions[0].Options.Select(o => o.Key));
        Assert.Equal(DateOnly.MinValue, stored.Questions[0].Options[0].Date);
        Assert.Equal(new TimeOnly(23, 59), stored.Questions[0].Options[4].Start);

        var submit = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(id, Anon("v1"),
            new DatesBallot(new Dictionary<string, Availability> { [FirstMidnight] = Availability.Yes, [LastMinute] = Availability.Maybe }), "Ann", null));
        Assert.True(submit.Succeeded);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(id, Anon("v2"),
            new DatesBallot(new Dictionary<string, Availability> { [LastMinute] = Availability.Yes }), "Bo", null))).Succeeded);

        var results = await WithScope(factory, async sp =>
        {
            var access = await sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(owner));
            return await sp.GetRequiredService<ResultsService>().ComputeAsync(access!);
        });
        Assert.Equal([FirstDay, FirstMidnight, LastDay, LastSpan, LastMinute], results.Dates!.Rows.Select(r => r.Key));
        Assert.Equal((1, 1, 0, 1), (results.Dates.Rows[4].Yes, results.Dates.Rows[4].Maybe, results.Dates.Rows[4].No, results.Dates.Rows[4].Place));

        // Pages: vote (anonymous), results (owner, in the viewer zone), edit (calendar at year 1).
        var anonymous = factory.CreateBrowser();
        var vote = await GetPageAsync(anonymous, $"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, vote.StatusCode);
        var voteHtml = WebUtility.HtmlDecode(await vote.Content.ReadAsStringAsync());
        Assert.Contains("January 0001", voteHtml);
        Assert.Contains("December 9999", voteHtml);
        Assert.Contains("Mon 0001-01-01 00:00", voteHtml);
        Assert.Contains("Fri 9999-12-31 23:59", voteHtml);

        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var resultsPage = await GetPageAsync(browser, $"/p/{id}/results");
        Assert.Equal(HttpStatusCode.OK, resultsPage.StatusCode);
        var resultsHtml = WebUtility.HtmlDecode(await resultsPage.Content.ReadAsStringAsync());
        Assert.Contains("<span class=\"dates-best-item\">Fri 9999-12-31 23:59 · 1 yes, 1 maybe</span>", resultsHtml);
        Assert.Contains("Mon 0001-01-01 00:00", resultsHtml);
        Assert.Contains(viewerZone, resultsHtml);

        var edit = await browser.GetStringAsync($"/polls/{id}/edit");
        var editHtml = WebUtility.HtmlDecode(edit);
        Assert.Contains("January 0001", editHtml);
        Assert.Contains("data-day=\"0001-01-01\"", editHtml);
        Assert.Contains("aria-label=\"Previous month\" title=\"Previous month\" disabled", editHtml);
        Assert.Contains("2 days · 5 options", editHtml);

        var ballots = Encoding.UTF8.GetString((await browser.GetByteArrayAsync($"/polls/{id}/export.csv"))[3..]).Split("\r\n");
        Assert.Equal("Name,Alias,SubmittedAt,UpdatedAt,0001-01-01,0001-01-01 00:00,9999-12-31,9999-12-31 23:00–23:59,9999-12-31 23:59", ballots[0]);
        Assert.StartsWith("Ann,,", ballots[1]);
        Assert.EndsWith(",no,yes,no,no,maybe", ballots[1]);
        var csv = Encoding.UTF8.GetString((await browser.GetByteArrayAsync($"/polls/{id}/results.csv"))[3..]).Split("\r\n");
        Assert.Equal("Place,Date,Yes,Maybe,No", csv[0]);
        Assert.Equal("2,0001-01-01 00:00,1,0,1", csv[2]);
        Assert.Equal("1,9999-12-31 23:59,1,1,0", csv[5]);
    }

    [Fact]
    public async Task A_vote_form_post_works_at_the_last_minute_of_9999()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, EdgeDraft("Pacific/Kiritimati"));
        var (client, jar) = factory.CreateBrowserWithJar();
        var page = await GetPageAsync(client, $"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var visitor = VisitorFrom(factory, jar, id);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(id, visitor,
            new DatesBallot(new Dictionary<string, Availability> { [LastMinute] = Availability.Yes, [FirstDay] = Availability.Maybe }), null, null))).Succeeded);
        var again = WebUtility.HtmlDecode(await (await GetPageAsync(client, $"/p/{id}")).Content.ReadAsStringAsync());
        Assert.Contains("Your availability", again);
    }

    [Fact]
    public async Task The_editor_renders_the_count_weekday_headings_and_paged_days()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        // 12 days, one with two slots: 13 options.
        var id = await CreatePollAsync(factory, owner,
            Dates("Many", "2026-11-01 to 2026-11-11", "2026-11-20 18:00", "2026-11-20 20:00").Closes((DateOnly?)null));
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync($"/polls/{id}/edit"));
        Assert.Contains("12 days · 13 options", html);
        Assert.Contains(">13 / 100<", html);
        Assert.Contains("aria-label=\"13 of 100 options\"", html);
        Assert.Contains("aria-label=\"Every Tuesday in November 2026\"", html);
        Assert.Contains("aria-label=\"Choose month\"", html);
        Assert.Contains("aria-label=\"Page 2\"", html);
        Assert.Contains("aria-current=\"page\" aria-label=\"Page 1\"", html);
        Assert.Equal(10, html.Split("class=\"editor-day\"").Length - 1); // 10 days per page
        Assert.Contains("aria-label=\"About Calendar\"", html);
        Assert.Contains("Pick a time for Sunday 1 November 2026", html);
        Assert.DoesNotContain(" style=\"", html);
        Assert.DoesNotContain("disabled=\"\" aria-pressed", html); // days are never disabled (keyboard reaches every day)
    }

    [Fact]
    public void The_options_limit_help_names_the_cap_of_dates_polls()
    {
        var help = LimitSettings.Fields.Single(f => f.Key == SettingKeys.LimitOptions).Help;
        Assert.Contains($"a dates poll has at most {Telrang.Core.Polls.PollRules.MaxDateOptions}", help);
    }
}
