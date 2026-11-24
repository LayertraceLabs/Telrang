using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

/// <summary>DESIGN.md §34.5: question entries in the change log, and the question named on option entries.</summary>
public class QuestionChangeLogTests
{
    private static QuestionDraft Q(string prompt, params string[] labels) => new()
    {
        Prompt = prompt,
        Options = labels.Select(l => new OptionDraft { Label = l }).ToList(),
    };

    private static PollDefinition Ok(PollDraft d, PollDefinition? existing = null)
    {
        var r = PollDraftValidator.Validate(d, PollLimits.Default, existing);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return r.Definition!;
    }

    private static PollDefinition Three() =>
        Ok(new PollDraft { Title = "T", Questions = [Q("Food?", "Pizza", "Pasta", "Salad"), Q("Drink?", "Beer", "Wine"), Q("Shirt size", "S", "M")] });

    private static PollDefinition Edited(PollDefinition def, Action<PollDraft> change)
    {
        var d = PollDraft.FromDefinition(def);
        change(d);
        return Ok(d, def);
    }

    [Fact]
    public void An_unchanged_poll_has_no_entries()
    {
        var def = Three();
        Assert.Empty(OptionChanges.Between(def, Edited(def, _ => { })));
    }

    [Fact]
    public void A_removed_question_names_its_key_and_last_prompt()
    {
        var def = Three();
        var changes = OptionChanges.Between(def, Edited(def, d => d.Questions.RemoveAt(1)));
        var c = Assert.Single(changes);
        Assert.Equal((OptionChangeKind.QuestionRemoved, "q2", "Drink?", "q2", "Drink?"), (c.Kind, c.Key, c.Label, c.QuestionKey, c.QuestionLabel));
    }

    [Fact]
    public void An_added_question_gets_the_next_key_and_no_option_entries()
    {
        var def = Three();
        var after = Edited(def, d => d.Questions.Add(Q("Dessert?", "Cake", "Fruit")));
        var c = Assert.Single(OptionChanges.Between(def, after));
        Assert.Equal((OptionChangeKind.QuestionAdded, "q4", "Dessert?"), (c.Kind, c.Key, c.Label));
    }

    [Fact]
    public void A_removed_and_a_re_added_question_are_two_entries_with_different_keys()
    {
        var def = Three();
        var after = Edited(def, d =>
        {
            d.Questions.RemoveAt(2);
            d.Questions.Add(Q("Shirt size", "S", "M"));
        });
        var changes = OptionChanges.Between(def, after);
        Assert.Equal([(OptionChangeKind.QuestionRemoved, "q3"), (OptionChangeKind.QuestionAdded, "q4")], changes.Select(c => (c.Kind, c.Key)));
    }

    [Fact]
    public void A_renamed_question_is_one_entry_and_case_and_spacing_do_not_count()
    {
        var def = Three();
        var changes = OptionChanges.Between(def, Edited(def, d => d.Questions[1].Prompt = "Beverage?"));
        var c = Assert.Single(changes);
        Assert.Equal((OptionChangeKind.QuestionRenamed, "q2", "Beverage?", "Drink?"), (c.Kind, c.Key, c.Label, c.OldLabel));
        Assert.Empty(OptionChanges.Between(def, Edited(def, d => d.Questions[1].Prompt = "  drink?  ")));
    }

    [Fact]
    public void Option_entries_name_their_question()
    {
        var def = Three();
        var after = Edited(def, d =>
        {
            d.Questions[0].Options.RemoveAt(0);
            d.Questions[2].Options.Add(new OptionDraft { Label = "L" });
            d.Questions[1].Options[0].Label = "Lager";
        });
        var changes = OptionChanges.Between(def, after);
        Assert.Equal(
            [
                (OptionChangeKind.Removed, "pizza", "q1", "Food?"),
                (OptionChangeKind.Renamed, "beer", "q2", "Drink?"),
                (OptionChangeKind.Added, "_1", "q3", "Shirt size"),
            ],
            changes.Select(c => (c.Kind, c.Key, c.QuestionKey!, c.QuestionLabel!)));
    }

    [Fact]
    public void Order_is_title_then_removed_questions_then_each_question_in_order()
    {
        var def = Three();
        var after = Edited(def, d =>
        {
            d.Title = "New title";
            d.Questions.RemoveAt(0);
            d.Questions[0].Prompt = "Drinks?";
            d.Questions[0].Options[1].Label = "Cider";
            d.Questions.Add(Q("Last?", "Yes", "No"));
        });
        Assert.Equal(
            [
                OptionChangeKind.TitleChanged,
                OptionChangeKind.QuestionRemoved,
                OptionChangeKind.QuestionRenamed,
                OptionChangeKind.Renamed,
                OptionChangeKind.QuestionAdded,
            ],
            OptionChanges.Between(def, after).Select(c => c.Kind));
    }

    [Fact]
    public void The_single_question_overload_stays_untagged()
    {
        var def = Three();
        var after = Edited(def, d => d.Questions[0].Options.RemoveAt(0));
        var c = Assert.Single(OptionChanges.Between(def.Questions[0], after.Questions[0]));
        Assert.Null(c.QuestionKey);
    }

    [Fact]
    public void Pages_groups_help_and_moves_are_not_logged()
    {
        var def = Three();
        var after = Edited(def, d =>
        {
            d.Pages = [new PageDraft { Key = "a", Title = "One" }, new PageDraft { Key = "b", Title = "Two" }];
            d.Questions[0].Page = "a";
            d.Questions[1].Page = "a";
            d.Questions[2].Page = "b";
            d.Questions[0].Help = "Some help";
        });
        Assert.Empty(OptionChanges.Between(def, after));
    }
}
