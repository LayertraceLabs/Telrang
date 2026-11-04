using System.Net;
using System.Text.Json.Nodes;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>The form editor (DESIGN.md §20): drafts in, field errors out, copies of existing polls.</summary>
public class EditorTests
{
    private static Task<T> Polls<T>(AppFactory factory, Func<PollService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<PollService>()));

    private static async Task<Poll> PollRowAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking().SingleAsync(p => p.Id == pollId));
    }

    // ---- create and edit -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_and_edit_store_the_draft_as_a_definition()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var draft = Points("Budget", 100, "Paris", "Rome").Describe("**Spend** wisely.").Q(q => { q.MaxPerOption = 60; q.SpendAll = true; })
            .Voters(VoterMode.Accounts).Many().Names(NamesMode.Required).Results(ResultsVisibility.AfterVote).Zone("Europe/Berlin")
            .With(d => { d.ResultsTiming = ResultsTiming.AfterClose; d.ShowBallots = true; });
        var id = await CreatePollAsync(factory, owner, draft);

        var row = await PollRowAsync(factory, id);
        Assert.Equal(("Budget", PollType.Points, VoterMode.Accounts, ResultsVisibility.AfterVote), (row.Title, row.Type, row.VoterMode, row.ResultsVisibility));
        var def = PollJson.DeserializeDefinition(row.DefinitionJson);
        Assert.Equal((ResultsTiming.AfterClose, true), (def.ResultsTiming, def.ShowBallots));
        Assert.Equal("**Spend** wisely.", def.DescriptionMarkdown);
        Assert.Equal((ResponsePolicy.Many, NamesMode.Required, "Europe/Berlin"), (def.Responses, def.Names, def.TimeZone));
        var q = Assert.Single(def.Questions);
        Assert.Equal(("q1", 100, 60, true), (q.Key, q.Budget, q.MaxPerOption, q.SpendAll));
        Assert.Equal(["paris", "rome"], q.Options.Select(o => o.Key));

        // The edit form starts from the stored definition and saves through the same validator.
        var model = (await Polls(factory, p => p.GetForEditAsync(owner, id)))!;
        var form = PollDraft.FromDefinition(model.Definition);
        Assert.Equal("Budget", form.Title);
        form.Title = "Holiday budget";
        form.Questions[0].Options[1].Description = "Pasta.";
        Assert.True((await UpdatePollAsync(factory, owner, id, form)).Succeeded);
        var after = PollJson.DeserializeDefinition((await PollRowAsync(factory, id)).DefinitionJson);
        Assert.Equal("Holiday budget", after.Title);
        Assert.Equal("Pasta.", after.Questions[0].Options[1].DescriptionMarkdown);
        Assert.Equal("Holiday budget", (await PollRowAsync(factory, id)).Title);
    }

    [Fact]
    public async Task Invalid_drafts_come_back_as_field_errors()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var draft = Points("", 0, "Paris", "Paris").Q(q => q.Options.Add(new OptionDraft { Label = " " })).Zone("Mars/Olympus");
        var result = await Polls(factory, p => p.CreatePollAsync(owner, draft));
        Assert.Equal(PollSaveStatus.Invalid, result.Status);
        Assert.Null(result.EncodedId);
        var fields = result.Errors.Select(e => e.Field).ToList();
        Assert.Contains("title", fields);
        Assert.Contains("questions[0].budget", fields);
        Assert.Contains("questions[0].options[1].label", fields);
        Assert.Contains("questions[0].options[2].label", fields);
        Assert.Contains("timezone", fields);
        Assert.Equal("Enter a title.", result.Errors.First(e => e.Field == "title").Message);

        // Edits report field errors the same way; type changes are refused on the type field.
        var id = await CreatePollAsync(factory, owner, Ranked("T", "A", "B"));
        var edit = await UpdatePollAsync(factory, owner, id, Dates("T", "2026-11-03", "2026-11-04"));
        Assert.Equal(PollSaveStatus.Invalid, edit.Status);
        Assert.Equal("questions[0].type", Assert.Single(edit.Errors).Field);
    }

    [Fact]
    public async Task Ranked_ballots_are_stored_under_the_question_key()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "v1", null), new RankedBallot(["rome", "paris"], ["berlin"]))).Succeeded);
        var payload = JsonNode.Parse(Assert.Single(await BallotsAsync(factory, id)).PayloadJson)!.AsObject();
        Assert.Equal(["q1"], payload.Select(p => p.Key));
        Assert.Equal("rome", payload["q1"]!["ranked"]![0]!.GetValue<string>());
        Assert.Equal("berlin", payload["q1"]!["banned"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Edit_keeps_ballots_for_option_changes_and_refuses_fixed_settings()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "v1", null), new RankedBallot(["paris"], ["berlin"]))).Succeeded);
        var payload = Assert.Single(await BallotsAsync(factory, id)).PayloadJson;

        // Reordering, new details, removing an option: saved, the ballot stays exactly as stored (DESIGN.md §26).
        var reordered = RankedDraft().Q(q => { q.Options.Reverse(); q.Options[0].Description = "Currywurst."; });
        Assert.True((await UpdatePollAsync(factory, owner, id, reordered)).Succeeded);
        Assert.True((await UpdatePollAsync(factory, owner, id, RankedDraft().Q(q => q.Options.RemoveAt(2)))).Succeeded);
        Assert.Equal(payload, Assert.Single(await BallotsAsync(factory, id)).PayloadJson);

        // A fixed setting is refused, nothing changes.
        var refused = await UpdatePollAsync(factory, owner, id, RankedDraft().Q(q => { q.Bans = 0; q.Options.RemoveAt(2); }));
        Assert.Equal(PollSaveStatus.Invalid, refused.Status);
        Assert.Contains(new DraftError("questions[0].bans", PollDraftValidator.FixedMessage("Bans per voter")), refused.Errors);
        Assert.Equal(payload, Assert.Single(await BallotsAsync(factory, id)).PayloadJson);
    }

    // ---- copy ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_takes_the_definition_and_nothing_else()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var source = RankedDraft(ResultsVisibility.AfterVote, VoterMode.Invite).Many().Names(NamesMode.Required).Zone("Europe/Berlin")
            .Q(q => { q.Scoring = ScoringScheme.Dowdall; q.RankAll = true; q.Options[0].Description = "Lights."; });
        var id = await CreatePollAsync(factory, owner, source);
        var invites = (await Polls(factory, p => p.GenerateInvitesAsync(owner, id, 0, ["Ann"])))!;
        Assert.True(InviteTokens.TryHash(Assert.Single(invites).Token, out var hash));
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, null, hash), new RankedBallot(["rome", "paris", "berlin"], []), "Ann")).Succeeded);
        Assert.True(await Polls(factory, p => p.SetClosedAsync(owner, id, true)));

        var copy = (await Polls(factory, p => p.GetCopyAsync(owner, id)))!;
        Assert.Equal(PollJson.Serialize(source.Zone("Europe/Berlin")), PollJson.Serialize(copy));

        // The new poll starts open, without ballots, invite links or aliases.
        var newId = await CreatePollAsync(factory, owner, copy);
        Assert.NotEqual(id, newId);
        Assert.False(PollState.IsClosed(await PollRowAsync(factory, newId), DateTimeOffset.UtcNow));
        Assert.Empty(await BallotsAsync(factory, newId));
        Assert.Empty((await Polls(factory, p => p.ListInvitesAsync(owner, newId)))!);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Copy_keeps_the_close_date_only_while_it_is_ahead_and_within_the_limit()
    {
        var clock = new MutableTime(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Closes("2030-03-01"));

        Assert.Equal(new DateOnly(2030, 3, 1), (await Polls(factory, p => p.GetCopyAsync(owner, id)))!.ClosesOn);

        // Beyond the owner's current max duration from now: dropped.
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitPollDurationDays] = "30" }, null);
        Assert.Null((await Polls(factory, p => p.GetCopyAsync(owner, id)))!.ClosesOn);
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitPollDurationDays] = "180" }, null);

        // In the past: dropped.
        clock.Now = new DateTimeOffset(2030, 3, 2, 0, 0, 0, TimeSpan.Zero);
        Assert.Null((await Polls(factory, p => p.GetCopyAsync(owner, id)))!.ClosesOn);
    }

    [Fact]
    public async Task Copy_is_for_the_owner_only_and_others_get_404()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "x@example.com");
        var other = await CreateUserAsync(factory, "y@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Titled("Team trip"));

        Assert.Null(await Polls(factory, p => p.GetCopyAsync(other, id)));
        Assert.Null(await Polls(factory, p => p.GetCopyAsync(owner, "AAAAAAAAAAAAAAAAAAAAAA")));

        var mine = await LoggedInBrowserAsync(factory, "o@example.com");
        var page = await mine.GetAsync($"/polls/new?from={id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Copy poll", html);
        Assert.Contains("value=\"Team trip\"", html);

        var theirs = await LoggedInBrowserAsync(factory, "x@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await theirs.GetAsync($"/polls/new?from={id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await theirs.GetAsync("/polls/new?from=nonsense")).StatusCode);
    }

    [Fact]
    public async Task Copy_links_on_my_polls_the_share_page_and_the_owners_vote_page()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var link = $"href=\"polls/new?from={id}\"";
        Assert.Contains(link, await browser.GetStringAsync("/polls"));
        Assert.Contains(link, await browser.GetStringAsync($"/polls/{id}/share"));
        Assert.Contains(link, await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());
        // Voters never see it.
        Assert.DoesNotContain("polls/new?from=", await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync());
    }

    // ---- pages ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editor_pages_render_the_form()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var create = await browser.GetStringAsync("/polls/new");
        foreach (var text in new[] { "Basics", "Ranked", "Points", "Dates", "Add option", "Who votes", "Closes on", "Time zone", "Preview" })
            Assert.Contains(text, create);
        Assert.DoesNotContain("style=", create);

        var id = await CreatePollAsync(factory, owner, Dates("Meetup", "2026-11-03", "2026-11-05 19:00"));
        var edit = await browser.GetStringAsync($"/polls/{id}/edit");
        Assert.Contains("Allow maybe", edit);
        Assert.Contains("aria-pressed=\"true\"", edit); // the selected days of the calendar
        Assert.Contains("Thu 2026-11-05", edit);
        Assert.Contains("aria-label=\"Poll type\"", create);
        Assert.DoesNotContain("aria-label=\"Poll type\"", edit); // the type is fixed after creation
    }
}
