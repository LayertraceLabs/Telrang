using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

/// <summary>DESIGN.md §34.2: pages, groups, question fields and keys, the structure rules, the edit rules per question.</summary>
public class QuestionStructureTests
{
    private static OptionDraft O(string label) => new() { Label = label };

    private static QuestionDraft Ranked(string prompt = "Q", string? page = null, string? group = null, string? key = null) => new()
    {
        Key = key,
        Prompt = prompt,
        Page = page,
        Group = group,
        Type = PollType.Ranked,
        Options = [O("A"), O("B")],
    };

    private static PageDraft Page(string key, string title = "Page") => new() { Key = key, Title = title };
    private static GroupDraft Group(string key, string title = "Group") => new() { Key = key, Title = title };

    private static PollDraft Draft(params QuestionDraft[] questions) => new() { Title = "T", Questions = [.. questions] };

    private static DraftResult Run(PollDraft d, PollDefinition? existing = null, PollLimits? limits = null) =>
        PollDraftValidator.Validate(d, limits ?? PollLimits.Default, existing);

    private static PollDefinition Ok(PollDraft d, PollDefinition? existing = null, PollLimits? limits = null)
    {
        var r = Run(d, existing, limits);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return r.Definition!;
    }

    private static IReadOnlyList<DraftError> Fail(PollDraft d, PollDefinition? existing = null, PollLimits? limits = null)
    {
        var r = Run(d, existing, limits);
        Assert.Null(r.Definition);
        return r.Errors;
    }

    private static void Has(IReadOnlyList<DraftError> errors, string field, string? contains = null) =>
        Assert.Contains(errors, e => e.Field == field && (contains is null || e.Message.Contains(contains)));

    private static PollDraft Structured() => new()
    {
        Title = "Trip",
        Pages = [Page("a", "Who"), Page("b", "What")],
        Groups = [Group("x", "You"), Group("y", "Partner")],
        Questions =
        [
            Ranked("Name?", "a", "x"), Ranked("Diet?", "a", "x"), Ranked("Partner name?", "a", "y"),
            Ranked("Food?", "b"),
        ],
    };

    // ---- defaults of a definition stored before §34 ----

    [Fact]
    public void A_single_simple_question_keeps_every_old_default()
    {
        var def = Ok(Draft(new QuestionDraft { Options = [O("A"), O("B")] }));
        var q = Assert.Single(def.Questions);
        Assert.Equal(("q1", "", "", false, null, null), (q.Key, q.Prompt, q.HelpMarkdown, q.Required, q.Page, q.Group));
        Assert.Empty(def.Pages);
        Assert.Empty(def.Groups);
        Assert.Equal(1, def.QuestionsIssued);
    }

    [Fact]
    public void A_definition_without_the_new_fields_validates_unchanged_on_edit()
    {
        // As stored before §34: no prompt, no counters, no pages.
        var old = new PollDefinition("T", "", [new PollQuestion("q1", PollType.Ranked, [new PollOption("a", "A", ""), new PollOption("b", "B", "")])]);
        var again = Ok(PollDraft.FromDefinition(old), old);
        Assert.Equal("q1", again.Questions[0].Key);
        Assert.Equal(["a", "b"], again.Questions[0].Options.Select(o => o.Key));
        Assert.Equal(1, again.QuestionsIssued);
        // A draft without keys (a browser draft from before §34) is still that question.
        var legacy = PollDraft.FromDefinition(old);
        legacy.Questions[0].Key = null;
        Assert.Equal("q1", Ok(legacy, old).Questions[0].Key);
    }

    // ---- prompt, help ----

    [Fact]
    public void Prompt_is_optional_for_one_question_and_required_for_several()
    {
        Ok(Draft(Ranked("")));
        var e = Fail(Draft(Ranked(""), Ranked("Two")));
        Has(e, "questions[0].prompt", "Enter the question");
        Assert.DoesNotContain(e, x => x.Field == "questions[1].prompt");
    }

    [Fact]
    public void Prompt_and_help_limits_and_hidden_characters()
    {
        var q = Ranked(new string('x', 201));
        q.Help = new string('y', 1001);
        var e = Fail(Draft(q));
        Has(e, "questions[0].prompt", "200");
        Has(e, "questions[0].help", "1000");
        Has(Fail(Draft(Ranked("a‮b"))), "questions[0].prompt", "invisible");
        var ok = Ranked(new string('x', 200));
        ok.Help = new string('y', 1000);
        Ok(Draft(ok));
    }

    [Fact]
    public void Prompt_help_and_required_are_stored()
    {
        var q = Ranked("  Food?  ");
        q.Help = "Pick **two**.\r\n";
        q.Required = true;
        var s = Ok(Draft(q)).Questions[0];
        Assert.Equal(("Food?", "Pick **two**.", true), (s.Prompt, s.HelpMarkdown, s.Required));
    }

    // ---- pages and groups ----

    [Fact]
    public void Pages_and_groups_get_keys_in_order()
    {
        var def = Ok(Structured());
        Assert.Equal(["p1", "p2"], def.Pages.Select(p => p.Key));
        Assert.Equal(["g1", "g2"], def.Groups.Select(g => g.Key));
        Assert.Equal(["Who", "What"], def.Pages.Select(p => p.Title));
        Assert.Equal([("p1", "g1"), ("p1", "g1"), ("p1", "g2"), ("p2", (string?)null)], def.Questions.Select(q => (q.Page!, q.Group)));
        Assert.Equal((4, 2, 2), (def.QuestionsIssued, def.PagesIssued, def.GroupsIssued));
    }

    [Fact]
    public void Questions_must_follow_page_order()
    {
        var d = Structured();
        d.Questions[3].Page = "a";
        d.Questions[0].Page = "b";
        d.Groups.Clear();
        foreach (var q in d.Questions) q.Group = null;
        Has(Fail(d), "questions[1].page", "above the questions of later pages");
    }

    [Fact]
    public void A_group_stays_together_on_one_page()
    {
        var d = Structured();
        d.Questions[2].Group = "x"; // x is contiguous with q1, q2 and still on page a
        d.Groups.RemoveAt(1);
        Ok(d);
        d.Questions[3].Group = "x"; // page b
        Has(Fail(d), "questions[3].group", "together");

        var apart = Structured();
        apart.Questions[1].Group = null; // x, none, x
        apart.Questions[2].Group = "x";
        Has(Fail(apart), "questions[2].group", "together");
    }

    [Fact]
    public void Every_page_and_group_needs_a_question_and_a_title()
    {
        var d = Structured();
        d.Pages.Add(Page("c", "Empty"));
        Has(Fail(d), "pages[2]", "Add a question");
        d.Pages.RemoveAt(2);
        d.Groups.Add(Group("z", "Empty"));
        Has(Fail(d), "groups[2]", "Add a question");
        d.Groups.RemoveAt(2);
        d.Pages[1].Title = " ";
        d.Groups[0].Title = new string('t', 121);
        var e = Fail(d);
        Has(e, "pages[1].title", "Enter a title");
        Has(e, "groups[0].title", "120");
    }

    [Fact]
    public void With_pages_every_question_names_one_and_references_must_exist()
    {
        var d = Structured();
        d.Questions[3].Page = null;
        Has(Fail(d), "questions[3].page", "Pick a page");
        d.Questions[3].Page = "nope";
        Has(Fail(d), "questions[3].page", "Pick a page");
        d = Structured();
        d.Questions[0].Group = "nope";
        Has(Fail(d), "questions[0].group", "Pick a group");
        // Without pages a question names none.
        var plain = Draft(Ranked("A"), Ranked("B", page: "a"));
        Has(Fail(plain), "questions[1].page");
    }

    [Fact]
    public void Groups_without_pages_live_on_the_implicit_page()
    {
        var d = Draft(Ranked("A", group: "x"), Ranked("B", group: "x"), Ranked("C"));
        d.Groups = [Group("x")];
        var def = Ok(d);
        Assert.Equal(["g1", "g1", null], def.Questions.Select(q => q.Group));
        Assert.Empty(def.Pages);
    }

    [Fact]
    public void Duplicate_draft_keys_and_limits()
    {
        var d = Structured();
        d.Pages[1].Key = "a";
        Has(Fail(d), "pages[1].key");

        var many = Draft(Enumerable.Range(0, 51).Select(i => Ranked("Q" + i)).ToArray());
        Has(Fail(many), "questions", "at most 50");
        var pages = Structured();
        pages.Pages = Enumerable.Range(0, 21).Select(i => Page("k" + i)).ToList();
        Has(Fail(pages), "pages", "at most 20");
        var groups = Structured();
        groups.Groups = Enumerable.Range(0, 31).Select(i => Group("k" + i)).ToList();
        Has(Fail(groups), "groups", "at most 30");
        Ok(Draft(Enumerable.Range(0, 50).Select(i => Ranked("Q" + i)).ToArray()));
    }

    // ---- edits: keys never come back ----

    [Fact]
    public void Edit_keeps_keys_and_numbers_new_ones_from_the_counters()
    {
        var def = Ok(Structured());
        var d = PollDraft.FromDefinition(def);
        d.Questions.RemoveAt(3); // q4 and with it page p2
        d.Pages.RemoveAt(1);
        d.Questions.Add(Ranked("New?", "p1"));
        d.Pages.Add(Page("fresh", "Later"));
        d.Questions.Add(Ranked("Newer?", "fresh"));
        d.Groups.Add(Group("fresh-g", "Box"));
        d.Questions[4].Group = "fresh-g";
        var edited = Ok(d, def);
        Assert.Equal(["q1", "q2", "q3", "q5", "q6"], edited.Questions.Select(q => q.Key));
        Assert.Equal(["p1", "p3"], edited.Pages.Select(p => p.Key));
        Assert.Equal(["g1", "g2", "g3"], edited.Groups.Select(g => g.Key));
        Assert.Equal((6, 3, 3), (edited.QuestionsIssued, edited.PagesIssued, edited.GroupsIssued));
        Assert.Equal("p3", edited.Questions[4].Page);
        // A key a draft names that the poll no longer has is a new item, never the old one.
        var back = PollDraft.FromDefinition(edited);
        back.Pages.Add(Page("p2", "Revived?"));
        back.Questions.Add(Ranked("Q", "p2", key: "q4"));
        var again = Ok(back, edited);
        Assert.Equal("q7", again.Questions[^1].Key);
        Assert.Equal("p4", again.Pages[^1].Key);
    }

    [Fact]
    public void Counters_start_from_the_highest_key_of_an_old_definition()
    {
        var old = new PollDefinition("T", "", [new PollQuestion("q3", PollType.Ranked, [new PollOption("a", "A", ""), new PollOption("b", "B", "")])]);
        var d = PollDraft.FromDefinition(old);
        d.Questions.Add(Ranked("Two"));
        d.Questions[0].Prompt = "One";
        Assert.Equal(["q3", "q4"], Ok(d, old).Questions.Select(q => q.Key));
    }

    [Fact]
    public void Edit_reorders_moves_between_pages_and_changes_prompt_and_help()
    {
        var def = Ok(Structured());
        var d = PollDraft.FromDefinition(def);
        (d.Questions[0], d.Questions[1]) = (d.Questions[1], d.Questions[0]);
        d.Questions[0].Prompt = "Changed";
        d.Questions[0].Help = "New help";
        d.Pages[1].Title = "Last";
        d.Questions.Add(Ranked("Last?", "p2"));
        var edited = Ok(d, def);
        Assert.Equal(["q2", "q1", "q3", "q4", "q5"], edited.Questions.Select(q => q.Key));
        Assert.Equal(("Changed", "New help"), (edited.Questions[0].Prompt, edited.Questions[0].HelpMarkdown));
        Assert.Equal("Last", edited.Pages[1].Title);
    }

    [Fact]
    public void Edit_fixes_type_and_rules_per_question()
    {
        var def = Ok(Structured());
        var d = PollDraft.FromDefinition(def);
        d.Questions[1].Type = PollType.Points;
        d.Questions[1].Budget = 10;
        Has(Fail(d, def), "questions[1].type", "cannot be changed");

        d = PollDraft.FromDefinition(def);
        d.Questions[2].Bans = 1;
        Has(Fail(d, def), "questions[2].bans", "cannot be changed");

        d = PollDraft.FromDefinition(def);
        d.Questions[0].Required = true;
        Has(Fail(d, def), "questions[0].required", "cannot be changed");

        // A new question is free: any type, any rules.
        d = PollDraft.FromDefinition(def);
        d.Questions.Add(new QuestionDraft { Type = PollType.Points, Prompt = "P", Budget = 5, Page = "p2", Options = [O("A"), O("B")] });
        Assert.Equal(PollType.Points, Ok(d, def).Questions[4].Type);
    }

    [Fact]
    public void Edit_cannot_remove_the_last_question()
    {
        var def = Ok(Structured());
        var d = PollDraft.FromDefinition(def);
        d.Questions.Clear();
        Has(Fail(d, def), "questions");
    }

    // ---- limits ----

    [Fact]
    public void Limits_have_the_documented_defaults()
    {
        var l = PollLimits.Default;
        Assert.Equal((50, 20, 30, 200, 1000, 2000, 8192), (l.QuestionsMax, l.PagesMax, l.GroupsMax, l.PromptMax, l.HelpMax, l.TextAnswerMax, l.ResponseTextBytesMax));
        Assert.Equal((120, 80, 1000, 2000, 100), (l.TitleMax, l.OptionLabelMax, l.OptionDescriptionMax, l.DescriptionMax, l.OptionsMax));
    }
}
