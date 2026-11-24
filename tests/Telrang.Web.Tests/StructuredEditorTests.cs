using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §34.1, §34.5 (step 4): the editor for polls with several questions, pages and groups, the new types in it, the
/// edit rules per question, the change log's question entries, the admin's structure limits and the size line in lists.
/// </summary>
public class StructuredEditorTests
{
    private static QuestionDraft Q(PollType type, string prompt, params string[] options) => new()
    {
        Type = type,
        Prompt = prompt,
        Budget = type == PollType.Points ? 10 : null,
        Options = options.Select(o => new OptionDraft { Label = o }).ToList(),
    };

    private static DraftResult Check(PollDraft d, PollDefinition? existing = null) => PollParsing.Validate(d, PollLimits.Default, existing: existing);

    private static async Task<Poll> RowAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking().SingleAsync(p => p.Id == pollId));
    }

    private static T WithScopeSync<T>(AppFactory factory, Func<IServiceProvider, T> action)
    {
        using var scope = factory.Services.CreateScope();
        return action(scope.ServiceProvider);
    }

    private static Task<PollEditModel?> EditModelAsync(AppFactory factory, string owner, string id) =>
        WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id));

    // ---- the outline's operations ---------------------------------------------------------------------

    [Fact]
    public void Add_question_leaves_the_simple_editor_and_names_the_first_question()
    {
        var d = Ranked("Lunch", "Pizza", "Pasta");
        Assert.True(DraftOutline.IsSimple(d));
        var added = DraftOutline.AddQuestion(d, PollLimits.Default, d.Questions[0])!;
        Assert.False(DraftOutline.IsSimple(d));
        Assert.Equal("Lunch", d.Questions[0].Prompt); // the title was the question
        Assert.Same(added, d.Questions[1]);
        Assert.Equal(PollType.Choice, added.Type);
        added.Prompt = "Drinks";
        added.Options = [new() { Label = "Water" }, new() { Label = "Juice" }];
        Assert.True(Check(d).Success, string.Join(", ", Check(d).Errors));

        Assert.True(DraftOutline.RemoveQuestion(d, added));
        Assert.True(DraftOutline.IsSimple(d));
        Assert.False(DraftOutline.RemoveQuestion(d, d.Questions[0])); // never the last one
    }

    [Fact]
    public void Add_page_puts_the_questions_so_far_on_page_one()
    {
        var d = Ranked("Lunch", "Pizza", "Pasta");
        var added = DraftOutline.AddPage(d, PollLimits.Default)!;
        Assert.Equal(2, d.Pages.Count);
        Assert.Equal(d.Pages[0].Key, d.Questions[0].Page);
        Assert.Equal(d.Pages[1].Key, added.Page);
        Assert.All(d.Pages, p => Assert.StartsWith("new-", p.Key));
        added.Prompt = "Drinks";
        added.Options = [new() { Label = "Water" }, new() { Label = "Juice" }];
        var def = Check(d).Definition!;
        Assert.Equal(["p1", "p2"], def.Pages.Select(p => p.Key));
        Assert.Equal(["Page 1", "Page 2"], def.Pages.Select(p => p.Title));

        // Removing a page moves its questions to the one before; removing the last page leaves none.
        DraftOutline.RemovePage(d, d.Pages[1]);
        Assert.All(d.Questions, q => Assert.Equal(d.Pages[0].Key, q.Page));
        DraftOutline.RemovePage(d, d.Pages[0]);
        Assert.Empty(d.Pages);
        Assert.All(d.Questions, q => Assert.Null(q.Page));
        Assert.True(Check(d).Success);
    }

    [Fact]
    public void Moves_step_into_and_out_of_groups_and_across_pages()
    {
        var d = Ranked("Lunch", "Pizza", "Pasta");
        var a = d.Questions[0];
        var b = DraftOutline.AddGroup(d, PollLimits.Default, a)!; // a, [b]
        var group = Assert.Single(d.Groups);
        Assert.Equal(group.Key, b.Group);
        var c = DraftOutline.AddQuestion(d, PollLimits.Default, b)!; // a, [b, c]
        (b.Prompt, c.Prompt) = ("B", "C"); // the drafts are records: distinct values, so the order checks below mean something
        Assert.Equal(group.Key, c.Group);
        Assert.Equal("Group 1", group.Title);

        DraftOutline.MoveUp(d, b); // b leaves the group above c
        Assert.Null(b.Group);
        Assert.Equal([a, b, c], d.Questions);
        DraftOutline.MoveDown(d, b); // and joins it again, first
        Assert.Equal(group.Key, b.Group);
        DraftOutline.MoveDown(d, b); // swaps with c inside the group
        Assert.Equal([a, c, b], d.Questions);
        DraftOutline.MoveDown(d, b); // leaves at the end
        Assert.Null(b.Group);
        Assert.False(DraftOutline.CanMoveDown(d, b));
        DraftOutline.MoveUp(d, a); // first of everything: nothing above
        Assert.Equal([a, c, b], d.Questions);

        DraftOutline.MoveUp(d, c); // c alone in the group: leaving it removes the empty group
        Assert.Null(c.Group);
        Assert.Empty(d.Groups);

        var p2 = DraftOutline.AddPage(d, PollLimits.Default)!; // page 1: a, c, b; page 2: p2
        p2.Prompt = "P2";
        Assert.True(DraftOutline.CanMoveUp(d, p2));
        DraftOutline.MoveUp(d, p2); // onto page 1, at its end
        Assert.Equal(d.Pages[0].Key, p2.Page);
        DraftOutline.MoveDown(d, p2); // and back to page 2
        Assert.Equal(d.Pages[1].Key, p2.Page);
        Assert.Equal([a, c, b, p2], d.Questions);
    }

    [Fact]
    public void A_page_moves_with_its_questions()
    {
        var d = Ranked("Lunch", "Pizza", "Pasta");
        var second = DraftOutline.AddPage(d, PollLimits.Default)!;
        DraftOutline.MovePage(d, d.Pages[1], -1);
        Assert.Same(second, d.Questions[0]);
        Assert.Equal(d.Pages[0].Key, second.Page);
    }

    [Fact]
    public void Adds_stop_at_the_limits()
    {
        var limits = PollLimits.Default with { QuestionsMax = 2, PagesMax = 1, GroupsMax = 0 };
        var d = Ranked("Lunch", "Pizza", "Pasta");
        Assert.Null(DraftOutline.AddPage(d, limits)); // a first page needs two
        Assert.Null(DraftOutline.AddGroup(d, limits));
        Assert.NotNull(DraftOutline.AddQuestion(d, limits));
        Assert.Null(DraftOutline.AddQuestion(d, limits));
    }

    // ---- saving, the edit rules per question, the change log ----------------------------------------------

    [Fact]
    public async Task A_structured_poll_is_created_with_its_size()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, MultiPageVoteTests.Survey());
        var row = await RowAsync(factory, id);
        Assert.Equal((PollType.Choice, 5, 2), (row.Type, row.QuestionCount, row.PageCount));

        var simple = await CreatePollAsync(factory, owner, new PollDraft { Title = "Age", Questions = [Q(PollType.Number, "")] });
        Assert.Equal((PollType.Number, 1, 1), ((await RowAsync(factory, simple)).Type, (await RowAsync(factory, simple)).QuestionCount, (await RowAsync(factory, simple)).PageCount));

        // An edit writes the size again.
        var model = (await EditModelAsync(factory, owner, id))!;
        var form = PollDraft.FromDefinition(model.Definition);
        form.Questions.RemoveAt(4);
        foreach (var q in form.Questions) q.Page = form.Pages[0].Key;
        form.Pages.RemoveAt(1);
        Assert.True((await UpdatePollAsync(factory, owner, id, form)).Succeeded);
        row = await RowAsync(factory, id);
        Assert.Equal((4, 1), (row.QuestionCount, row.PageCount));

        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var list = WebUtility.HtmlDecode(await (await GetPageAsync(browser, "/polls")).Content.ReadAsStringAsync());
        Assert.Contains("4 questions ·", list);
        Assert.Contains("Number ·", list);
        var share = WebUtility.HtmlDecode(await (await GetPageAsync(browser, $"/polls/{id}/share")).Content.ReadAsStringAsync());
        Assert.Contains("4 questions · Anyone with the link", share);
    }

    [Fact]
    public void The_size_line_names_pages_and_questions()
    {
        Assert.Equal("Ranked", PollText.KindLine(PollType.Ranked, 1, 1));
        Assert.Equal("4 pages · 9 questions", PollText.KindLine(PollType.Choice, 9, 4));
        Assert.Equal("2 questions", PollText.KindLine(PollType.Choice, 2, 1));
        Assert.Equal("2 pages · 1 question", PollText.SizeLine(2, 1));
    }

    /// <summary>
    /// After the first ballot (DESIGN.md §34.5): a question renamed, one removed, one added; the change log records each
    /// (removal with the ballots that answered it), names the question on option entries, and the editor's preview is the same.
    /// </summary>
    [Fact]
    public async Task Question_changes_after_voting_go_into_the_change_log()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, MultiPageVoteTests.Survey());
        await StructuredResultsTests.SubmitAsync(factory, id, new VisitorContext(null, "voter-key-one-000000000000000000000", null),
            StructuredResultsTests.Answers(("q1", new QuestionAnswer { Choice = new ChoiceBallot(["yes"]) }), ("q4", new QuestionAnswer { Text = "Hi" })), null);
        await StructuredResultsTests.SubmitAsync(factory, id, new VisitorContext(null, "voter-key-two-000000000000000000000", null),
            StructuredResultsTests.Answers(("q1", new QuestionAnswer { Choice = new ChoiceBallot(["no"]) })), null);

        var model = (await EditModelAsync(factory, owner, id))!;
        Assert.Equal(1, model.Answers!.Questions["q4"]);
        Assert.Equal(2, model.Answers.Questions["q1"]);
        Assert.Equal(1, model.Answers.Options["q1"]["yes"]);
        var form = PollDraft.FromDefinition(model.Definition);
        form.Questions[0].Prompt = "Are you coming?";
        form.Questions[0].Options.RemoveAt(1); // "No", on one ballot
        form.Questions[0].Options.Add(new OptionDraft { Label = "Maybe" });
        form.Questions.RemoveAt(3); // Notes, answered on one ballot
        form.Questions.Add(new QuestionDraft { Prompt = "Shirt size", Type = PollType.Choice, Page = form.Pages[1].Key, Options = [new() { Label = "S" }, new() { Label = "M" }] });

        var preview = PollService.Changes(model.Definition, Check(form, model.Definition).Definition!);
        var lines = preview.Select(c => PollText.ChangeText(c.Kind, c.Label, c.OldLabel, PollText.IsRemoval(c.Kind) ? model.Answers.For(c) : null, c.QuestionLabel)).ToList();
        Assert.Equal(
        [
            "Question “Notes” removed (answered on 1 ballot)",
            "Question “Coming?” renamed to “Are you coming?”",
            "Are you coming?: “No” removed (on 1 ballot)",
            "Are you coming?: “Maybe” added",
            "Question “Shirt size” added",
        ], lines);

        var saved = await UpdatePollAsync(factory, owner, id, form);
        Assert.True(saved.Succeeded, string.Join(", ", saved.Errors));
        Assert.True(PollId.TryDecode(id, out var pollId));
        var rows = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().PollChanges.AsNoTracking()
            .Where(c => c.PollId == pollId).OrderBy(c => c.Seq).ToListAsync());
        Assert.Equal(lines, rows.Select(c => PollText.ChangeText(c.Kind, c.Label, c.OldLabel, c.BallotCount, c.QuestionLabel)));
        Assert.Equal(["q4", "q1", "q1", "q1", "q6"], rows.Select(c => c.QuestionKey));

        // The vote page shows the entries (the counts are for those who see the results: the owner).
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var page = WebUtility.HtmlDecode(await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());
        Assert.Contains("Question “<bdi>Notes</bdi>” removed (answered on 1 ballot)", page);
        Assert.Contains("<bdi>Are you coming?</bdi>: “<bdi>No</bdi>” removed (on 1 ballot)", page);
    }

    [Fact]
    public void Option_entries_of_a_simple_poll_name_no_question()
    {
        var before = Definition(Ranked("Lunch", "Pizza", "Pasta", "Salad"));
        var after = Check(PollDraft.FromDefinition(before).Q(q => q.Options.RemoveAt(0)), before).Definition!;
        var change = Assert.Single(PollService.Changes(before, after));
        Assert.Null(change.QuestionLabel);
        Assert.Equal("q1", change.QuestionKey);
    }

    [Fact]
    public async Task Stored_questions_keep_their_type_and_rules_and_new_ones_are_free()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, MultiPageVoteTests.Survey());
        var stored = (await EditModelAsync(factory, owner, id))!.Definition;

        // A browser draft that changed a stored question's rules, and added a question with rules of its own.
        var draft = PollDraft.FromDefinition(stored);
        draft.Questions[1].Min = 3;
        draft.Questions[1].Required = true;
        draft.Questions[2].Type = PollType.Ranked;
        draft.Questions.Add(new QuestionDraft { Prompt = "Age", Type = PollType.Number, Page = draft.Pages[1].Key, Min = 18, Required = true });
        Assert.False(Check(draft, stored).Success); // the validator refuses the changes

        PollEditor.PinFixed(draft, stored);
        Assert.Equal(((decimal?)0m, false), (draft.Questions[1].Min, draft.Questions[1].Required));
        Assert.Equal(PollType.Choice, draft.Questions[2].Type);
        Assert.Equal((PollType.Number, (decimal?)18m, true), (draft.Questions[5].Type, draft.Questions[5].Min, draft.Questions[5].Required));
        var result = await UpdatePollAsync(factory, owner, id, draft);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        var after = (await EditModelAsync(factory, owner, id))!.Definition;
        Assert.Equal("q6", after.Questions[5].Key);
        Assert.Equal(18m, after.Questions[5].Min);
    }

    [Fact]
    public void A_one_question_draft_without_a_key_is_pinned_to_the_stored_question()
    {
        var stored = Definition(new PollDraft { Title = "Lunch", Questions = [new QuestionDraft { Type = PollType.Choice, Options = [new() { Label = "A" }, new() { Label = "B" }] }] });
        var draft = new PollDraft { Title = "Lunch", Questions = [new QuestionDraft { Type = PollType.Text }] };
        PollEditor.PinFixed(draft, stored);
        Assert.Equal(("q1", PollType.Choice), (draft.Questions[0].Key, draft.Questions[0].Type));
    }

    // ---- copies, browser drafts, limits -------------------------------------------------------------

    [Fact]
    public async Task A_copy_keeps_the_structure_with_new_keys()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, MultiPageVoteTests.Survey());
        var copy = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetCopyAsync(owner, id)))!;
        Assert.Equal(2, copy.Pages.Count);
        Assert.Single(copy.Groups);
        Assert.All(copy.Questions, q => Assert.Null(q.Key));
        var again = await CreatePollAsync(factory, owner, copy);
        Assert.Equal((5, 2), ((await RowAsync(factory, again)).QuestionCount, (await RowAsync(factory, again)).PageCount));
    }

    [Fact]
    public void A_restored_draft_must_fit_the_structure_limits()
    {
        var l = PollLimits.Default;
        Assert.True(PollEditor.DraftFits(MultiPageVoteTests.Survey(), l));
        Assert.False(PollEditor.DraftFits(MultiPageVoteTests.Survey().With(d => d.Questions.AddRange(Enumerable.Range(0, l.QuestionsMax).Select(_ => new QuestionDraft()))), l));
        Assert.False(PollEditor.DraftFits(MultiPageVoteTests.Survey().With(d => d.Pages.AddRange(Enumerable.Range(0, l.PagesMax).Select(_ => new PageDraft()))), l));
        Assert.False(PollEditor.DraftFits(MultiPageVoteTests.Survey().With(d => d.Groups.AddRange(Enumerable.Range(0, l.GroupsMax).Select(_ => new GroupDraft()))), l));
        Assert.False(PollEditor.DraftFits(MultiPageVoteTests.Survey().With(d => d.Questions[0].Prompt = new string('a', 2 * l.PromptMax + 1)), l));
        Assert.False(PollEditor.DraftFits(MultiPageVoteTests.Survey().With(d => d.Questions[0].Help = new string('a', 2 * l.HelpMax + 1)), l));
        Assert.False(PollEditor.DraftFits(MultiPageVoteTests.Survey().With(d => d.Pages[0].Title = new string('a', 2 * l.TitleMax + 1)), l));

        var big = MultiPageVoteTests.Survey().With(d => { d.Questions[0].Prompt = new string('a', 900); d.Groups[0].Description = new string('b', 9000); });
        PollEditor.ClampTexts(big, l);
        Assert.Equal(2 * l.PromptMax, big.Questions[0].Prompt.Length);
        Assert.Equal(2 * l.DescriptionMax, big.Groups[0].Description.Length);
    }

    [Fact]
    public async Task The_structure_limits_are_admin_settings()
    {
        await using var factory = new AppFactory();
        var settings = factory.Services.GetRequiredService<SettingsService>();
        await settings.SetAsync(new Dictionary<string, string?>
        {
            [SettingKeys.LimitQuestions] = "4",
            [SettingKeys.LimitPages] = "1",
            [SettingKeys.LimitGroups] = "2",
            [SettingKeys.LimitPrompt] = "50",
            [SettingKeys.LimitHelp] = "60",
            [SettingKeys.LimitTextAnswer] = "70",
            [SettingKeys.LimitResponseText] = "80",
        }, null);
        var limits = WithScopeSync(factory, sp => sp.GetRequiredService<PollService>().CurrentLimits);
        Assert.Equal((4, 1, 2, 50, 60, 70, 80), (limits.QuestionsMax, limits.PagesMax, limits.GroupsMax, limits.PromptMax, limits.HelpMax, limits.TextAnswerMax, limits.ResponseTextBytesMax));

        var owner = await CreateUserAsync(factory, "o@example.com");
        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(owner, MultiPageVoteTests.Survey()));
        Assert.Equal(PollSaveStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == "questions");

        // Ceilings: stored values above them are read as the ceiling, and the admin form refuses them.
        await settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitQuestions] = "500", [SettingKeys.LimitResponseText] = "100000" }, null);
        limits = WithScopeSync(factory, sp => sp.GetRequiredService<PollService>().CurrentLimits);
        Assert.Equal((VoteFormLimits.QuestionsMax, VoteFormLimits.FreeTextBytes), (limits.QuestionsMax, limits.ResponseTextBytesMax));
        var d = PollLimits.Default;
        foreach (var (key, expected) in new[] { (SettingKeys.LimitQuestions, d.QuestionsMax), (SettingKeys.LimitPages, d.PagesMax),
                     (SettingKeys.LimitGroups, d.GroupsMax), (SettingKeys.LimitPrompt, d.PromptMax), (SettingKeys.LimitHelp, d.HelpMax),
                     (SettingKeys.LimitTextAnswer, d.TextAnswerMax), (SettingKeys.LimitResponseText, d.ResponseTextBytesMax) })
        {
            var field = LimitSettings.Fields.Single(f => f.Key == key);
            Assert.Equal(expected, field.Default);
            Assert.True(LimitSettings.Ceilings[key] >= expected, key);
            Assert.Contains("At most", field.Help);
        }
    }

    [Fact]
    public void The_default_structure_limits_are_the_design_ones()
    {
        var s = new LimitSettings(0, 0, 0, 0, 0, 120, 80, 1000, 2000, 100, 60);
        Assert.Equal(PollLimits.Default, PollParsing.LimitsFrom(s));
    }

    // ---- the editor's markup ---------------------------------------------------------------------------

    private static async Task<string> RenderEditorAsync(AppFactory factory, PollDraft draft)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<PollEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PollEditor.Initial)] = draft,
            }));
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    [Fact]
    public async Task The_simple_editor_offers_every_type_and_the_add_bar()
    {
        await using var factory = new AppFactory();
        var html = await RenderEditorAsync(factory, RankedDraft());
        foreach (var type in new[] { "Choice", "Checkboxes", "Ranked", "Points", "Dates", "Text", "Number" })
            Assert.Contains($">{type}</label>", html);
        foreach (var add in new[] { "Add question", "Add group", "Add page" })
            Assert.Contains(add, html);
        Assert.DoesNotContain("role=\"tablist\"", html);
        Assert.DoesNotContain(" style=\"", html);
    }

    [Fact]
    public async Task The_structured_editor_shows_the_outline_and_the_first_question()
    {
        await using var factory = new AppFactory();
        var html = await RenderEditorAsync(factory, MultiPageVoteTests.Survey());
        Assert.Contains("role=\"tablist\"", html);
        foreach (var tab in new[] { "Questions", "Voting and results", "Preview" })
            Assert.Contains($">{tab}", html);
        Assert.Contains("2 pages · 5 questions", html);
        Assert.Contains("1 · Attending", html);
        Assert.Contains("Group · You", html);
        Assert.Contains("<span class=\"outline-type\">Checkboxes</span>", html); // Diet: more than one pick
        Assert.Contains("Page 1 · Group: You · Question 1 of 5", html);
        Assert.Contains("aria-label=\"About Fewest picks\"", html); // the first question's editor: a choice
        Assert.DoesNotContain(" style=\"", html);
    }
}
