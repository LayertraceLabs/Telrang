using System.Net;
using System.Security.Claims;
using System.Text;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §26: fixed settings, option changes that keep ballots, removed options ignored on read, the change log.</summary>
public class ChangeLogTests
{
    private const string Heading = "Changed after voting started";

    private static PollDraft Lunch(VoterMode voters = VoterMode.Public) =>
        Ranked("Lunch", "Pizza", "Pasta", "Salad").Voters(voters).Results(ResultsVisibility.AfterVote);

    private static Task<T> Polls<T>(AppFactory factory, Func<PollService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<PollService>()));

    private static async Task<PollDraft> CurrentAsync(AppFactory factory, string owner, string id) =>
        PollDraft.FromDefinition((await Polls(factory, p => p.GetForEditAsync(owner, id)))!.Definition);

    private static async Task<PollSaveResult> EditAsync(AppFactory factory, string owner, string id, Action<PollDraft> change)
    {
        var draft = await CurrentAsync(factory, owner, id);
        change(draft);
        return await UpdatePollAsync(factory, owner, id, draft);
    }

    private static async Task<List<PollChange>> LogAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return (await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().PollChanges.AsNoTracking()
            .Where(c => c.PollId == pollId).ToListAsync())).OrderBy(c => c.At).ThenBy(c => c.Seq).ToList();
    }

    /// <summary>The page decoded, with the change log's <c>&lt;bdi&gt;</c> isolation (DESIGN.md §31) taken out.</summary>
    private static async Task<string> PageAsync(HttpClient client, string path) =>
        Unisolated(WebUtility.HtmlDecode(await (await GetPageAsync(client, path)).Content.ReadAsStringAsync()));

    internal static string Unisolated(string html) => html.Replace("<bdi>", "", StringComparison.Ordinal).Replace("</bdi>", "", StringComparison.Ordinal);

    private static Task<PollAccess?> AccessAsync(AppFactory factory, string id, VisitorContext visitor) =>
        WithScope(factory, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id, visitor));

    [Fact]
    public async Task Removed_options_are_ignored_on_read_and_the_ballot_stays_raw()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Lunch());
        var me = VisitorContext.ForUser(voter);
        Assert.True((await SubmitRankedAsync(factory, id, me, new RankedBallot(["pizza", "pasta", "salad"], []), "Vi")).Succeeded);
        var raw = Assert.Single(await BallotsAsync(factory, id)).PayloadJson;

        var saved = await EditAsync(factory, owner, id, d => d.Questions[0].Options.RemoveAt(0)); // Pizza
        Assert.True(saved.Succeeded);
        Assert.Equal(raw, Assert.Single(await BallotsAsync(factory, id)).PayloadJson);

        // Results: the options below Pizza moved up (Borda over 2 options).
        var results = await WithScope(factory, async sp => await sp.GetRequiredService<ResultsService>().ComputeAsync((await AccessAsync(factory, id, me))!));
        Assert.Equal([("pasta", 2.0, 1), ("salad", 1.0, 0)], results.Ranked!.Rows.Select(r => (r.Key, r.Score, r.FirstChoiceCount)));

        // The voter's own ballot, its summary and the edit form show only current options.
        var state = (await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, me)))!;
        Assert.Equal(["pasta", "salad"], Assert.Single(state.Responses).Ranked!.Ranked);
        var browser = await LoggedInBrowserAsync(factory, "v@example.com");
        var summary = await PageAsync(browser, $"/p/{id}");
        Assert.DoesNotContain("Pizza", summary.Replace("“Pizza” removed", "", StringComparison.Ordinal));
        var form = await PageAsync(browser, $"/p/{id}?edit=1");
        Assert.Contains("name=\"o.pasta\"", form);
        Assert.DoesNotContain("name=\"o.pizza\"", form);
        Assert.Matches("name=\"o.pasta\"[^>]*value=\"1\"|value=\"1\"[^>]*name=\"o.pasta\"", form);

        // The ballots CSV: one column per current option, positions compacted.
        var csv = Encoding.UTF8.GetString((await (await LoggedInBrowserAsync(factory, "o@example.com")).GetByteArrayAsync($"/polls/{id}/export.csv"))[3..]).Split("\r\n");
        Assert.EndsWith(",Pasta,Salad", csv[0]);
        Assert.EndsWith(",1,2", csv[1]);
    }

    [Fact]
    public async Task Points_on_a_removed_option_no_longer_count_also_under_spend_all()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, PointsDraft(ResultsVisibility.AfterVote).Q(q => q.SpendAll = true));
        var me = VisitorContext.ForUser(voter);
        Assert.True((await SubmitPointsAsync(factory, id, me, new() { ["paris"] = 6, ["rome"] = 4 })).Succeeded);
        Assert.True((await EditAsync(factory, owner, id, d => d.Questions[0].Options.RemoveAt(0))).Succeeded);

        var results = await WithScope(factory, async sp => await sp.GetRequiredService<ResultsService>().ComputeAsync((await AccessAsync(factory, id, me))!));
        Assert.Equal([("rome", 4L), ("berlin", 0L)], results.Points!.Rows.Select(r => (r.Key, r.Total)));
        var mine = Assert.Single((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, me)))!.Responses);
        Assert.Equal(["rome"], mine.Points!.Points.Keys);
        Assert.Contains("4 of 10 points used.", await PageAsync(await LoggedInBrowserAsync(factory, "v@example.com"), $"/p/{id}"));

        // Changing the ballot: the whole budget again, on the options there are.
        Assert.Equal(SubmitStatus.Invalid, (await SubmitPointsAsync(factory, id, me, new() { ["rome"] = 4 })).Status);
        Assert.True((await SubmitPointsAsync(factory, id, me, new() { ["rome"] = 4, ["berlin"] = 6 })).Succeeded);
    }

    [Fact]
    public async Task A_new_option_under_rank_all_reads_as_unranked_and_the_voter_can_rank_it()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Lunch().Q(q => q.RankAll = true));
        var me = VisitorContext.ForUser(voter);
        Assert.True((await SubmitRankedAsync(factory, id, me, new RankedBallot(["pizza", "pasta", "salad"], []))).Succeeded);
        Assert.True((await EditAsync(factory, owner, id, d => d.Questions[0].Options.Add(new OptionDraft { Label = "Soup" }))).Succeeded);

        // Counted as unranked, shown in the form as unranked.
        var me2 = await AccessAsync(factory, id, me);
        var results = await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().ComputeAsync(me2!));
        Assert.Equal(0, results.Ranked!.Rows.Single(r => r.Key == "_1").Score); // the first key an edit issues (DESIGN.md §31)
        var form = await PageAsync(await LoggedInBrowserAsync(factory, "v@example.com"), $"/p/{id}?edit=1");
        Assert.Contains("name=\"o._1\"", form);

        // The old ranking alone is refused (rank all); with the new option ranked it saves.
        Assert.Equal(SubmitStatus.Invalid, (await SubmitRankedAsync(factory, id, me, new RankedBallot(["pizza", "pasta", "salad"], []))).Status);
        Assert.True((await SubmitRankedAsync(factory, id, me, new RankedBallot(["_1", "pizza", "pasta", "salad"], []))).Succeeded);
    }

    [Fact]
    public async Task Change_log_starts_with_the_first_ballot_and_records_option_changes()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Lunch());

        // No ballot yet: nothing is recorded.
        Assert.True((await EditAsync(factory, owner, id, d => d.Questions[0].Options.Add(new OptionDraft { Label = "Soup" }))).Succeeded);
        Assert.Empty(await LogAsync(factory, id));

        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "v1", null), new RankedBallot(["pizza"], []))).Succeeded);
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "v2", null), new RankedBallot(["pasta", "_1"], []))).Succeeded);

        // Close date, reorder, and title and rename changes in case and spacing only: not recorded (DESIGN.md §31).
        var quiet = await EditAsync(factory, owner, id, d =>
        {
            d.Title = "LUNCH";
            d.ClosesOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
            d.Questions[0].Options.Reverse();
            d.Questions[0].Options.Single(o => o.Label == "Pasta").Label = "PASTA";
        });
        Assert.True(quiet.Succeeded);
        Assert.Empty(quiet.Changes!);
        Assert.Empty(await LogAsync(factory, id));
        Assert.Contains((await CurrentAsync(factory, owner, id)).Questions[0].Options, o => o.Label == "PASTA");
        Assert.True(await Polls(factory, p => p.SetClosedAsync(owner, id, true)));
        Assert.True(await Polls(factory, p => p.SetClosedAsync(owner, id, false)));
        Assert.Empty(await LogAsync(factory, id));

        // Option changes: removed (with the ballots that answered it), renamed, new details, added.
        var saved = await EditAsync(factory, owner, id, d =>
        {
            var o = d.Questions[0].Options;
            o.RemoveAll(x => x.Label == "Pizza");
            o.Single(x => x.Label == "Salad").Label = "Green salad";
            o.Single(x => x.Label == "Soup").Description = "Tomato.";
            o.Add(new OptionDraft { Label = "Curry" });
        });
        Assert.True(saved.Succeeded);
        Assert.Single((await BallotsAsync(factory, id)), b => b.PayloadJson.Contains("pizza", StringComparison.Ordinal));
        var log = await LogAsync(factory, id);
        Assert.Equal(
            [
                (OptionChangeKind.Removed, "Pizza", (string?)null, (int?)1),
                (OptionChangeKind.DetailsChanged, "Soup", null, null), // the options' order after the reversal above
                (OptionChangeKind.Renamed, "Green salad", "Salad", null),
                (OptionChangeKind.Added, "Curry", null, null),
            ],
            log.Select(c => (c.Kind, c.Label, c.OldLabel, c.BallotCount)));
        Assert.All(log, c => Assert.Equal(log[0].At, c.At));
        Assert.Equal(saved.Changes!.Count, log.Count);
    }

    [Fact]
    public async Task Change_log_shows_to_everyone_who_can_view_the_poll()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Lunch().Results(ResultsVisibility.Creator));
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "v1", null), new RankedBallot(["pizza"], []))).Succeeded);
        Assert.True((await EditAsync(factory, owner, id, d => d.Questions[0].Options.RemoveAt(0))).Succeeded);

        var visitor = factory.CreateBrowser();
        var vote = await PageAsync(visitor, $"/p/{id}");
        Assert.Contains(Heading, vote);
        // The ballot count only for viewers who can see the results (DESIGN.md §31): here only the owner.
        Assert.Contains("“Pizza” removed", vote);
        Assert.DoesNotContain("(on 1 ballot)", vote);
        Assert.DoesNotContain(" style=\"", vote);
        // The results page shows it too, also when the results themselves are hidden.
        var results = await GetPageAsync(visitor, $"/p/{id}/results");
        Assert.Equal(HttpStatusCode.Forbidden, results.StatusCode);
        var hidden = Unisolated(WebUtility.HtmlDecode(await results.Content.ReadAsStringAsync()));
        Assert.Contains("“Pizza” removed", hidden);
        Assert.DoesNotContain("(on 1 ballot)", hidden);
        var own = await PageAsync(await LoggedInBrowserAsync(factory, "o@example.com"), $"/p/{id}/results");
        Assert.Contains(Heading, own);
        Assert.Contains("“Pizza” removed (on 1 ballot)", own);

        // A poll without changes shows no heading.
        var other = await CreatePollAsync(factory, owner, Lunch());
        Assert.DoesNotContain(Heading, await PageAsync(visitor, $"/p/{other}"));
    }

    [Fact]
    public async Task Change_log_is_kept_on_transfer_not_copied_and_deleted_with_the_poll()
    {
        await using var factory = new AppFactory();
        var adminId = await CreateUserAsync(factory, "admin@example.com");
        var admin = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(adminId))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
        var owner = await CreateUserAsync(factory, "o@example.com");
        var heir = await CreateUserAsync(factory, "h@example.com");
        var id = await CreatePollAsync(factory, owner, Lunch());
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "v1", null), new RankedBallot(["pizza"], []))).Succeeded);
        Assert.True((await EditAsync(factory, owner, id, d => d.Questions[0].Options.Add(new OptionDraft { Label = "Soup" }))).Succeeded);
        Assert.Single(await LogAsync(factory, id));

        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<AdminService>().TransferPollAsync(admin, id, "h@example.com"))).Ok);
        Assert.Single(await LogAsync(factory, id));

        var copy = await CreatePollAsync(factory, heir, (await Polls(factory, p => p.GetCopyAsync(heir, id)))!);
        Assert.Empty(await LogAsync(factory, copy));

        Assert.True(await Polls(factory, p => p.DeletePollAsync(heir, id)));
        Assert.Empty(await LogAsync(factory, id));
    }

    [Fact]
    public async Task Edit_page_shows_the_fixed_settings_read_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, PointsDraft(ResultsVisibility.AfterVote, VoterMode.Invite).Names(NamesMode.Required)
            .With(d => { d.ResultsTiming = ResultsTiming.AfterClose; d.ShowBallots = true; }));
        var html = await PageAsync(await LoggedInBrowserAsync(factory, "o@example.com"), $"/polls/{id}/edit");
        Assert.Contains("Fixed when the poll was created.", html);
        Assert.Contains("#lock", html);
        foreach (var (label, value) in new[] { ("Poll type", "Points"), ("Budget", "10"), ("Who votes", "Invite links"), ("Names", "Required"),
                     ("Results for", "Voters"), ("Show results", "After closing"), ("Show ballots to voters", "Yes") })
            Assert.Matches($@"editor-fixed-label"">{label}</span>[\s\S]*?editor-fixed-value"">{value}</span>", html);
        // No controls for them: the type switch, the voter radios and the budget field are gone; options and close date stay.
        Assert.DoesNotContain("aria-label=\"Poll type\"", html);
        Assert.DoesNotContain(">Invite links</label>", html);
        Assert.DoesNotContain("f-questions-0-budget\"", html);
        Assert.Contains("Add option", html);
        Assert.Contains("Closes on", html);
        Assert.DoesNotContain(" style=\"", html);
    }

    [Fact]
    public async Task Change_text_and_preview_come_from_the_same_diff()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Lunch());
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "v1", null), new RankedBallot(["pizza", "pasta"], []))).Succeeded);
        var model = (await Polls(factory, p => p.GetForEditAsync(owner, id)))!;
        Assert.Equal(1, model.AnswersPerKey!["pizza"]);
        Assert.Equal(0, model.AnswersPerKey["salad"]);
        var after = PollDraft.FromDefinition(model.Definition);
        after.Questions[0].Options.RemoveAt(0);
        var def = PollParsing.Validate(after, PollLimits.Default, existing: model.Definition).Definition!;
        var change = Assert.Single(PollService.Changes(model.Definition, def));
        Assert.Equal("“Pizza” removed (on 1 ballot)", PollText.ChangeText(change.Kind, change.Label, change.OldLabel, model.AnswersPerKey[change.Key]));
        Assert.Equal("“Salad” removed (on no ballots)", PollText.ChangeText(OptionChangeKind.Removed, "Salad", null, 0));
        Assert.Equal("“A” renamed to “B”", PollText.ChangeText(OptionChangeKind.Renamed, "B", "A", null));
        Assert.Equal("Details of “A” changed", PollText.ChangeText(OptionChangeKind.DetailsChanged, "A", null, null));
        Assert.Equal("“A” added", PollText.ChangeText(OptionChangeKind.Added, "A", null, null));
    }

    [Fact]
    public async Task Browser_drafts_tolerate_option_keys_old_and_new()
    {
        var old = """{"title":"T","questions":[{"type":"Ranked","options":[{"label":"A","description":""},{"label":"B","description":""}]}]}""";
        var draft = PollJson.DeserializeDraft(old);
        Assert.NotNull(draft);
        Assert.All(draft!.Questions[0].Options, o => Assert.Null(o.Key));
        var withKeys = PollJson.Serialize(draft.Q(q => q.Options[0].Key = "a"));
        Assert.Contains("\"key\":\"a\"", withKeys);
        Assert.Equal("a", PollJson.DeserializeDraft(withKeys)!.Questions[0].Options[0].Key);
        await Task.CompletedTask;
    }

    [Fact]
    public void Text_mode_carries_keys_by_exact_label()
    {
        var parsed = new List<OptionDraft> { new() { Label = "Pasta" }, new() { Label = "Pizza Napoli" }, new() { Label = "Pasta" } };
        PollEditor.CarryKeys(parsed, [("Pizza Napoli", "pizza"), ("Pasta", "pasta")], PollType.Ranked);
        Assert.Equal(["pasta", "pizza", null], parsed.Select(o => o.Key));

        var dates = new List<OptionDraft> { new() { Date = new DateOnly(2026, 11, 3), Start = new TimeOnly(19, 0) } };
        PollEditor.CarryKeys(dates, [("2026-11-03 19:00", "2026-11-03-19-00")], PollType.Dates);
        Assert.Equal("2026-11-03-19-00", dates[0].Key);
    }

    [Fact]
    public void A_restored_draft_on_edit_gets_the_stored_fixed_settings()
    {
        var stored = Definition(Lunch().Names(NamesMode.Required).Zone("Europe/Berlin"));
        var draft = Lunch().Voters(VoterMode.Invite).Names(NamesMode.None).Zone("UTC").Q(q => { q.Bans = 2; q.Type = PollType.Points; });
        PollEditor.PinFixed(draft, stored);
        Assert.Equal((VoterMode.Public, NamesMode.Required, "Europe/Berlin"), (draft.VoterMode, draft.Names, draft.TimeZone));
        Assert.Equal((PollType.Ranked, 0), (draft.Questions[0].Type, draft.Questions[0].Bans));
        Assert.True(PollDraftValidator.Validate(draft, PollLimits.Default, stored).Success);
    }
}
