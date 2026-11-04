using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Dates polls in the web app (DESIGN.md §18): storage, voting, results, CSV, pages.</summary>
public class DatesTests
{
    // Sorted by the parser: 2026-11-03, 2026-11-05 19:00, 2026-11-07 18:30–21:00.
    private const string Day = "2026-11-03", Evening = "2026-11-05-19-00", Span = "2026-11-07-18-30-21-00";

    private static PollDraft DatesDraft(ResultsVisibility results = ResultsVisibility.Creator, VoterMode voters = VoterMode.Public) =>
        Dates("Meetup", "2026-11-05 19:00", "2026-11-03", "  Whole day.", "2026-11-07 18:30-21:00").Describe("When?").Results(results).Voters(voters);

    private static Dictionary<string, Availability> A(params (string Key, Availability Value)[] answers) =>
        answers.ToDictionary(a => a.Key, a => a.Value);

    private static Task<SubmitResult> SubmitDatesAsync(AppFactory factory, string id, VisitorContext visitor,
        Dictionary<string, Availability> answers, string? name = null, int? responseNo = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(id, visitor, new DatesBallot(answers), name, responseNo));

    private static Task<PollResults> ResultsAsync(AppFactory factory, string id, VisitorContext viewer) =>
        WithScope(factory, async sp =>
        {
            var access = await sp.GetRequiredService<PollAccessService>().GetAsync(id, viewer);
            return await sp.GetRequiredService<ResultsService>().ComputeAsync(access!);
        });

    private static Task<BallotTable?> TableAsync(AppFactory factory, string id, VisitorContext viewer) =>
        WithScope(factory, async sp =>
        {
            var access = await sp.GetRequiredService<PollAccessService>().GetAsync(id, viewer);
            return await sp.GetRequiredService<ResultsService>().BallotsAsync(access!, 1);
        });

    private static VisitorContext Anon(string key) => new(null, key, null);

    // ---- storage ------------------------------------------------------------------------------------

    [Fact]
    public void Definition_and_ballot_round_trip_through_PollJson()
    {
        var def = Definition(DatesDraft().Q(q => q.Maybe = false));
        var back = PollJson.DeserializeDefinition(PollJson.Serialize(def));
        var q = Assert.Single(back.Questions);
        Assert.Equal("q1", q.Key);
        Assert.Equal(PollType.Dates, q.Type);
        Assert.False(q.Maybe);
        Assert.Equal(def.Questions[0].Options.Select(o => (o.Key, o.Label, o.Date, o.Start, o.End, o.DescriptionMarkdown)),
            q.Options.Select(o => (o.Key, o.Label, o.Date, o.Start, o.End, o.DescriptionMarkdown)));
        Assert.Equal(new TimeOnly(18, 30), q.Options[2].Start);
        Assert.Equal(new TimeOnly(21, 0), q.Options[2].End);
        Assert.Equal(new DateOnly(2026, 11, 3), q.Options[0].Date);

        var ballot = new DatesBallot(A((Day, Availability.Yes), (Evening, Availability.Maybe)));
        var json = PollJson.Serialize("q1", ballot);
        var read = PollJson.DeserializeDates(json, "q1");
        Assert.Equal(Availability.Yes, read.Answers[Day]);
        Assert.Equal(Availability.Maybe, read.Answers[Evening]);
        Assert.Contains("\"Maybe\"", json); // enums as names, like the rest of the stored JSON
    }

    [Fact]
    public void Ballot_payloads_are_keyed_by_question()
    {
        var def = Definition(DatesDraft());
        var json = PollJson.Serialize("q1", new DatesBallot(A((Day, Availability.Yes))));
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(["q1"], root.Select(p => p.Key));
        Assert.Equal("Yes", root["q1"]!["answers"]![Day]!.GetValue<string>());
        // A missing part reads as an empty answer; unknown option keys are left out on read (DESIGN.md §26).
        Assert.Empty(PollJson.DeserializeDates("{}", "q1").Answers);
        var mixed = PollJson.Serialize("q1", new DatesBallot(A((Day, Availability.Yes), ("removed-day", Availability.Yes))));
        Assert.Equal([Day], PollJson.ReadDates(mixed, def.Questions[0]).Answers.Keys);
        Assert.Equal(2, PollJson.DeserializeDates(mixed, "q1").Answers.Count); // stored raw
    }

    // ---- voting -------------------------------------------------------------------------------------

    [Fact]
    public async Task Submit_edit_and_validation()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft(results: ResultsVisibility.AfterVote));
        var (client, jar) = factory.CreateBrowserWithJar();
        await GetPageAsync(client, $"/p/{id}");
        var visitor = VisitorFrom(factory, jar, id);

        Assert.Equal(SubmitStatus.Invalid, (await SubmitDatesAsync(factory, id, visitor, A(("2026-12-24", Availability.Yes)))).Status);
        Assert.Empty(await BallotsAsync(factory, id));

        var ok = await SubmitDatesAsync(factory, id, visitor, A((Day, Availability.Yes), (Evening, Availability.Maybe), (Span, Availability.No)));
        Assert.True(ok.Succeeded);
        Assert.True(ok.Access!.CanSeeResults);
        var stored = PollJson.DeserializeDates(Assert.Single(await BallotsAsync(factory, id)).PayloadJson, "q1");
        Assert.Equal(2, stored.Answers.Count); // "no" is not stored: a missing key reads as no

        Assert.True((await SubmitDatesAsync(factory, id, visitor, A((Span, Availability.Yes)))).Succeeded);
        var state = (await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, visitor)))!;
        var mine = Assert.Single(state.Responses);
        Assert.Null(mine.Ranked);
        Assert.Null(mine.Points);
        Assert.Equal(Availability.Yes, mine.Dates!.Answers[Span]);
        Assert.False(mine.Dates.Answers.ContainsKey(Day));

        // An all-no ballot is an answer too.
        Assert.True((await SubmitDatesAsync(factory, id, visitor, A())).Succeeded);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync($"/p/{id}")).Content.ReadAsStringAsync());
        Assert.Contains("You have already voted", html);
        Assert.Contains("Your availability", html);
        Assert.Contains("dates-ballot", html);
    }

    [Fact]
    public async Task Maybe_is_refused_when_the_poll_says_maybe_no()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft().Q(q => q.Maybe = false));
        var refused = await SubmitDatesAsync(factory, id, Anon("v1"), A((Day, Availability.Maybe)));
        Assert.Equal(SubmitStatus.Invalid, refused.Status);
        Assert.Empty(await BallotsAsync(factory, id));
        Assert.True((await SubmitDatesAsync(factory, id, Anon("v1"), A((Day, Availability.Yes)))).Succeeded);

        var html = await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Answer yes or no per date.", html);
        Assert.Contains("value=\"yes\"", html);
        Assert.DoesNotContain("value=\"maybe\"", html); // no maybe answer without maybe
    }

    [Fact]
    public void Allow_maybe_is_fixed_after_creation()
    {
        var with = Definition(DatesDraft());
        var refused = PollDraftValidator.Validate(DatesDraft().Q(q => q.Maybe = false), PollLimits.Default, with);
        Assert.Contains(new DraftError("questions[0].maybe", PollDraftValidator.FixedMessage("Allow maybe")), refused.Errors);
    }

    [Fact]
    public async Task Many_responses_add_edit_and_delete()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft().Many());
        var me = await CreateUserAsync(factory, "me@example.com");
        var visitor = VisitorContext.ForUser(me);

        Assert.True((await SubmitDatesAsync(factory, id, visitor, A((Day, Availability.Yes)), "Ann")).Succeeded);
        Assert.True((await SubmitDatesAsync(factory, id, visitor, A((Evening, Availability.Yes)), "Bob")).Succeeded);
        Assert.True((await SubmitDatesAsync(factory, id, visitor, A((Span, Availability.Maybe)), "Bob", responseNo: 2)).Succeeded);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);

        var deleted = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().DeleteResponseAsync(id, visitor, 1));
        Assert.True(deleted.Succeeded);
        var left = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(("Bob", 2), (left.DisplayName, left.ResponseNo));
        Assert.Equal(Availability.Maybe, PollJson.DeserializeDates(left.PayloadJson, "q1").Answers[Span]);
    }

    [Fact]
    public async Task Vote_page_renders_the_switcher_and_three_views()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft());

        var response = await GetPageAsync(factory.CreateBrowser(), $"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("style-src 'self';", response.Headers.GetValues("Content-Security-Policy").Single());
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Answer yes, maybe or no per date.", html);
        Assert.Contains("class=\"dates-switch\"", html);
        foreach (var view in new[] { "calendar", "list", "timeline" })
        {
            Assert.Contains($"dates-switch-btn dates-switch-{view}", html);
            Assert.Contains($"dates-panel dates-{view}", html);
            // Inside the vote form the switch is a submit button (no JS: re-render, save nothing; DESIGN.md §21.1).
            Assert.Contains($"name=\"view\" value=\"{view}\"", html);
        }
        Assert.Contains("data-panel=\"list\" hidden", html);   // calendar first, the others hidden
        Assert.Contains("<caption>November 2026</caption>", html);
        Assert.Contains("aria-pressed=\"false\"", html);
        Assert.Contains("dates-seg", html);                   // list view: segmented radio group
        Assert.Contains("type=\"radio\"", html);
        Assert.Contains("18:30–21:00", html);                 // calendar slot label
        Assert.Contains("Tue 2026-11-03", html);              // list label with weekday
        Assert.Contains("Whole day.", html);                  // option description
        Assert.DoesNotContain(" style=\"", html);
    }

    // ---- results ------------------------------------------------------------------------------------

    [Fact]
    public async Task Results_count_answers_and_place_and_the_grid_is_owner_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft(results: ResultsVisibility.Open));
        Assert.True((await SubmitDatesAsync(factory, id, Anon("v1"), A((Day, Availability.Yes), (Evening, Availability.Yes)), "Ann")).Succeeded);
        Assert.True((await SubmitDatesAsync(factory, id, Anon("v2"), A((Day, Availability.Maybe), (Evening, Availability.Yes)), " ")).Succeeded);
        Assert.True((await SubmitDatesAsync(factory, id, Anon("v3"), A((Day, Availability.Yes), (Span, Availability.Maybe)), "Cy")).Succeeded);

        var r = await ResultsAsync(factory, id, VisitorContext.ForUser(owner));
        Assert.Equal(3, r.VoterCount);
        Assert.Null(r.Ranked);
        Assert.Null(r.Points);
        Assert.Equal([Day, Evening, Span], r.Dates!.Rows.Select(x => x.Key));
        Assert.Equal((2, 1, 0, 1), (r.Dates.Rows[0].Yes, r.Dates.Rows[0].Maybe, r.Dates.Rows[0].No, r.Dates.Rows[0].Place));
        Assert.Equal((2, 0, 1, 2), (r.Dates.Rows[1].Yes, r.Dates.Rows[1].Maybe, r.Dates.Rows[1].No, r.Dates.Rows[1].Place));
        Assert.Equal((0, 1, 2, 3), (r.Dates.Rows[2].Yes, r.Dates.Rows[2].Maybe, r.Dates.Rows[2].No, r.Dates.Rows[2].Place));

        var grid = (await TableAsync(factory, id, VisitorContext.ForUser(owner)))!.Dates!;
        Assert.Equal(["Ann", DatesGridRow.AnonymousName, "Cy"], grid.Select(g => g.Name));
        Assert.Equal([Availability.Yes, Availability.Yes, Availability.No], grid[0].Answers);
        Assert.Equal([Availability.Maybe, Availability.Yes, Availability.No], grid[1].Answers);

        // Not the owner: counts only.
        Assert.Null(await TableAsync(factory, id, Anon("v1")));
        Assert.NotNull((await ResultsAsync(factory, id, Anon("v1"))).Dates);

        var ownerBrowser = await LoggedInBrowserAsync(factory, "o@example.com");
        var ownerHtml = WebUtility.HtmlDecode(await (await ownerBrowser.GetAsync($"/p/{id}/results")).Content.ReadAsStringAsync());
        Assert.Contains("Best:", ownerHtml);
        Assert.Contains("Tue 2026-11-03 · 2 yes, 1 maybe", ownerHtml);
        Assert.Contains("dates-best", ownerHtml);
        Assert.Contains("dates-stack-yes", ownerHtml);
        Assert.Contains("dates-switch", ownerHtml);
        Assert.DoesNotContain("Who can when", ownerHtml);
        Assert.Contains($"href=\"p/{id}/results?view=ballots\"", ownerHtml);
        var ownerBallots = WebUtility.HtmlDecode(await (await ownerBrowser.GetAsync($"/p/{id}/results?view=ballots")).Content.ReadAsStringAsync());
        Assert.Contains("Who can when", ownerBallots);
        Assert.Contains("Anonymous", ownerBallots);
        Assert.Contains(">Ann<", ownerBallots);
        Assert.DoesNotContain("dates-stack-yes", ownerBallots);

        var response = await factory.CreateBrowser().GetAsync($"/p/{id}/results");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("style-src 'self';", response.Headers.GetValues("Content-Security-Policy").Single());
        var publicHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Best:", publicHtml);
        Assert.DoesNotContain("Who can when", publicHtml);
        Assert.DoesNotContain("Ann", publicHtml);
        Assert.DoesNotContain(" style=\"", publicHtml);
    }

    [Fact]
    public async Task Owner_grid_names_rows_anonymous_when_the_poll_collects_no_names()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft().Names(NamesMode.None));
        Assert.True((await SubmitDatesAsync(factory, id, Anon("v1"), A((Day, Availability.Yes)))).Succeeded);
        var r = await ResultsAsync(factory, id, VisitorContext.ForUser(owner));
        Assert.Equal(1, r.Dates!.Rows[0].Yes);
        // DESIGN.md §33: the owner's ballots view has the grid also without names.
        var row = Assert.Single((await TableAsync(factory, id, VisitorContext.ForUser(owner)))!.Dates!);
        Assert.Equal(DatesGridRow.AnonymousName, row.Name);
    }

    [Fact]
    public async Task Results_visibility_is_unchanged()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft(results: ResultsVisibility.AfterVote));
        var (client, jar) = factory.CreateBrowserWithJar();
        await GetPageAsync(client, $"/p/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/p/{id}/results")).StatusCode);
        Assert.True((await SubmitDatesAsync(factory, id, VisitorFrom(factory, jar, id), A((Evening, Availability.Yes)))).Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/p/{id}/results")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/p/AAAAAAAAAAAAAAAAAAAAAA/results")).StatusCode);

        var creatorOnly = await CreatePollAsync(factory, owner, DatesDraft());
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateBrowser().GetAsync($"/p/{creatorOnly}/results")).StatusCode);
    }

    [Fact]
    public async Task Csv_exports_write_answers_and_places()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft());
        Assert.True((await SubmitDatesAsync(factory, id, Anon("v1"), A((Day, Availability.Yes), (Evening, Availability.Maybe)), "Ann")).Succeeded);
        Assert.True((await SubmitDatesAsync(factory, id, Anon("v2"), A((Evening, Availability.Yes)), "Bob")).Succeeded);

        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var ballots = Encoding.UTF8.GetString((await browser.GetByteArrayAsync($"/polls/{id}/export.csv"))[3..]).Split("\r\n");
        Assert.Equal("Name,Alias,SubmittedAt,UpdatedAt,2026-11-03,2026-11-05 19:00,2026-11-07 18:30–21:00", ballots[0]);
        Assert.StartsWith("Ann,,", ballots[1]);
        Assert.EndsWith(",yes,maybe,no", ballots[1]);
        Assert.EndsWith(",no,yes,no", ballots[2]);

        var results = Encoding.UTF8.GetString((await browser.GetByteArrayAsync($"/polls/{id}/results.csv"))[3..]).Split("\r\n");
        Assert.Equal("Place,Date,Yes,Maybe,No", results[0]);
        Assert.Equal("2,2026-11-03,1,0,1", results[1]);
        Assert.Equal("1,2026-11-05 19:00,1,1,0", results[2]);
        Assert.Equal("3,2026-11-07 18:30–21:00,0,0,2", results[3]);
    }

    // ---- pages --------------------------------------------------------------------------------------

    [Fact]
    public async Task Closed_poll_shows_the_own_answers_read_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft(results: ResultsVisibility.AfterVote));
        var (browser, jar) = factory.CreateBrowserWithJar();
        await GetPageAsync(browser, $"/p/{id}");
        Assert.True((await SubmitDatesAsync(factory, id, VisitorFrom(factory, jar, id), A((Day, Availability.Yes), (Span, Availability.Maybe)), "Ann")).Succeeded);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));

        var html = WebUtility.HtmlDecode(await (await browser.GetAsync($"/p/{id}")).Content.ReadAsStringAsync());
        Assert.Contains("Poll closed.", html);
        Assert.Contains("Your ballot", html);
        Assert.Contains("dates-ballot", html);
        Assert.Contains("dates-cell-static", html);
        Assert.Contains("data-state=\"yes\"", html);
        Assert.Contains("data-state=\"maybe\"", html);
        Assert.DoesNotContain("<button type=\"button\" class=\"dates-cell", html);
        Assert.DoesNotContain("Submit ballot", html);
        Assert.Contains($"href=\"p/{id}/results\"", html);
    }

    [Fact]
    public async Task My_votes_and_the_admin_polls_tab_name_the_type()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, DatesDraft());
        var me = await CreateUserAsync(factory, "me@example.com");
        Assert.True((await SubmitDatesAsync(factory, id, VisitorContext.ForUser(me), A((Day, Availability.Yes)))).Succeeded);

        var list = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(me));
        Assert.Equal(PollType.Dates, Assert.Single(list).Type);
        var mine = await (await (await LoggedInBrowserAsync(factory, "me@example.com")).GetAsync("/my/votes")).Content.ReadAsStringAsync();
        Assert.Contains("Meetup", mine);
        Assert.Contains("Dates ·", mine);

        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.AddToRoleAsync((await users.FindByIdAsync(owner))!, AppRoles.Admin)).Succeeded;
        });
        var admin = await (await (await LoggedInBrowserAsync(factory, "o@example.com")).GetAsync("/admin?tab=polls")).Content.ReadAsStringAsync();
        Assert.Contains("Meetup", admin);
        Assert.Contains("Dates ·", admin);
    }

    [Fact]
    public void Poll_text_names_and_rules()
    {
        Assert.Equal("Dates", PollText.TypeName(PollType.Dates));
        var def = Definition(DatesDraft());
        Assert.Equal("Answer yes, maybe or no per date.", PollText.RulesLine(def.Questions[0]));
        Assert.Equal("Answer yes or no per date.", PollText.RulesLine(def.Questions[0] with { Maybe = false }));
        Assert.Equal("Thu 2026-11-05 19:00", DatesFormat.LongLabel(def.Questions[0].Options[1]));
        Assert.Equal("3", DatesFormat.SlotLabel(def.Questions[0].Options[0]));
        Assert.Equal("18:30–21:00", DatesFormat.SlotLabel(def.Questions[0].Options[2]));
        Assert.Equal(Availability.Yes, DatesFormat.Next(Availability.No, true));
        Assert.Equal(Availability.Maybe, DatesFormat.Next(Availability.Yes, true));
        Assert.Equal(Availability.No, DatesFormat.Next(Availability.Maybe, true));
        Assert.Equal(Availability.No, DatesFormat.Next(Availability.Yes, false));
    }
}
