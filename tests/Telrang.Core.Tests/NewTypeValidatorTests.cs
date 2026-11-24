using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

/// <summary>DESIGN.md §34.3: the settings of Choice, Text and Number questions, and the edit rules for them.</summary>
public class NewTypeValidatorTests
{
    private static OptionDraft O(string label) => new() { Label = label };

    private static QuestionDraft Choice(Action<QuestionDraft>? change = null)
    {
        var q = new QuestionDraft { Type = PollType.Choice, Options = [O("Red"), O("Green"), O("Blue")] };
        change?.Invoke(q);
        return q;
    }

    private static QuestionDraft Text(Action<QuestionDraft>? change = null)
    {
        var q = new QuestionDraft { Type = PollType.Text };
        change?.Invoke(q);
        return q;
    }

    private static QuestionDraft Number(Action<QuestionDraft>? change = null)
    {
        var q = new QuestionDraft { Type = PollType.Number };
        change?.Invoke(q);
        return q;
    }

    private static PollDraft Draft(QuestionDraft q) => new() { Title = "T", Questions = [q] };

    private static PollDefinition Ok(PollDraft d, PollDefinition? existing = null, PollLimits? limits = null)
    {
        var r = PollDraftValidator.Validate(d, limits ?? PollLimits.Default, existing);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return r.Definition!;
    }

    private static PollQuestion OkQ(QuestionDraft q, PollLimits? limits = null) => Ok(Draft(q), null, limits).Questions[0];

    private static IReadOnlyList<DraftError> Fail(PollDraft d, PollDefinition? existing = null, PollLimits? limits = null)
    {
        var r = PollDraftValidator.Validate(d, limits ?? PollLimits.Default, existing);
        Assert.Null(r.Definition);
        return r.Errors;
    }

    private static IReadOnlyList<DraftError> Fail(QuestionDraft q, PollLimits? limits = null) => Fail(Draft(q), null, limits);

    private static void Has(IReadOnlyList<DraftError> errors, string field, string? contains = null) =>
        Assert.Contains(errors, e => e.Field == field && (contains is null || e.Message.Contains(contains)));

    // ---- Choice ----

    [Fact]
    public void Choice_defaults_to_one_selection_as_buttons()
    {
        var q = OkQ(Choice());
        Assert.Equal((1, 1, false, ChoiceDisplay.Buttons), (q.MinSelect, q.MaxSelect, q.AllowOther, q.Display));
        Assert.Equal(["red", "green", "blue"], q.Options.Select(o => o.Key));
    }

    [Fact]
    public void Choice_with_several_selections_is_checkboxes()
    {
        var q = OkQ(Choice(c => { c.MinSelect = 2; c.MaxSelect = 3; c.AllowOther = true; }));
        Assert.Equal((2, 3, true), (q.MinSelect, q.MaxSelect, q.AllowOther));
    }

    [Fact]
    public void Choice_selection_bounds()
    {
        Has(Fail(Choice(c => c.MinSelect = 0)), "questions[0].minSelect");
        Has(Fail(Choice(c => c.MaxSelect = 0)), "questions[0].maxSelect");
        Has(Fail(Choice(c => { c.MinSelect = 3; c.MaxSelect = 2; })), "questions[0].minSelect", "maximum or less");
        Has(Fail(Choice(c => { c.MinSelect = 1; c.MaxSelect = 4; })), "questions[0].maxSelect", "number of options (3)");
        OkQ(Choice(c => { c.MinSelect = 1; c.MaxSelect = 3; })); // all options
        OkQ(Choice(c => { c.MinSelect = 1; c.MaxSelect = 4; c.AllowOther = true; })); // Other counts as a selection
    }

    [Fact]
    public void Dropdown_needs_a_maximum_of_one()
    {
        Assert.Equal(ChoiceDisplay.Dropdown, OkQ(Choice(c => c.Display = ChoiceDisplay.Dropdown)).Display);
        Has(Fail(Choice(c => { c.Display = ChoiceDisplay.Dropdown; c.MaxSelect = 2; })), "questions[0].display", "dropdown");
        Has(Fail(Choice(c => c.Display = (ChoiceDisplay)9)), "questions[0].display");
    }

    [Fact]
    public void Choice_needs_two_options_with_the_option_rules()
    {
        Has(Fail(Choice(c => c.Options = [O("Only")])), "questions[0].options", "at least 2");
        Has(Fail(Choice(c => c.Options = [O("A"), O("a")])), "questions[0].options[1].label", "twice");
        Has(Fail(Choice(c => c.Options = [O("A"), O("B​c")])), "questions[0].options[1].label");
    }

    // ---- Text ----

    [Fact]
    public void Text_has_no_options_and_the_admin_limit_as_default_maximum()
    {
        var q = OkQ(Text(t => t.Options = [O("ignored")]));
        Assert.Empty(q.Options);
        Assert.Equal((false, null, 2000), (q.Multiline, q.MinLength, q.MaxLength));
        Assert.Equal(300, OkQ(Text(), PollLimits.Default with { TextAnswerMax = 300 }).MaxLength);
    }

    [Fact]
    public void Text_length_bounds()
    {
        var q = OkQ(Text(t => { t.Multiline = true; t.MinLength = 5; t.MaxLength = 50; }));
        Assert.Equal((true, 5, 50), (q.Multiline, q.MinLength, q.MaxLength));
        Assert.Null(OkQ(Text(t => t.MinLength = 0)).MinLength);
        Has(Fail(Text(t => t.MinLength = -1)), "questions[0].minLength");
        Has(Fail(Text(t => t.MaxLength = 0)), "questions[0].maxLength", "between 1 and 2000");
        Has(Fail(Text(t => t.MaxLength = 2001)), "questions[0].maxLength", "2000");
        Has(Fail(Text(t => { t.MinLength = 10; t.MaxLength = 5; })), "questions[0].minLength", "maximum or less");
    }

    // ---- Number ----

    [Fact]
    public void Number_settings()
    {
        var q = OkQ(Number(n => { n.Min = 0; n.Max = 99.5m; n.Decimals = 1; n.Unit = " kg "; }));
        Assert.Equal((0m, 99.5m, 1, "kg"), (q.Min, q.Max, q.Decimals, q.Unit));
        Assert.Empty(q.Options);
        var open = OkQ(Number());
        Assert.Equal((null, null, 0, ""), (open.Min, open.Max, open.Decimals, open.Unit));
    }

    [Fact]
    public void Number_setting_errors()
    {
        Has(Fail(Number(n => { n.Min = 5; n.Max = 1; })), "questions[0].min", "maximum or less");
        Has(Fail(Number(n => n.Decimals = 7)), "questions[0].decimals", "0 to 6");
        Has(Fail(Number(n => n.Decimals = -1)), "questions[0].decimals");
        Has(Fail(Number(n => n.Unit = new string('u', 17))), "questions[0].unit", "16");
        Has(Fail(Number(n => n.Unit = "k‮g")), "questions[0].unit");
        Has(Fail(Number(n => n.Min = 1.5m)), "questions[0].min", "whole number");
        Has(Fail(Number(n => { n.Max = 2.123m; n.Decimals = 2; })), "questions[0].max", "2 decimal");
        Has(Fail(Number(n => n.Max = 1e16m)), "questions[0].max");
        OkQ(Number(n => { n.Min = -1e15m; n.Max = 1e15m; n.Decimals = 6; }));
    }

    // ---- editing ----

    private static PollDefinition Stored(params QuestionDraft[] questions)
    {
        var d = new PollDraft { Title = "T", Questions = [.. questions.Select((q, i) => { q.Prompt = "Q" + i; return q; })] };
        return Ok(d);
    }

    [Fact]
    public void Choice_rules_are_fixed_but_options_change()
    {
        var def = Stored(Choice(c => { c.MinSelect = 1; c.MaxSelect = 2; c.AllowOther = true; }));
        var d = PollDraft.FromDefinition(def);
        d.Questions[0].Options.Add(O("Pink"));
        d.Questions[0].Options.RemoveAt(0);
        Assert.Equal(["green", "blue", "_1"], Ok(d, def).Questions[0].Options.Select(o => o.Key));

        void Changes(Action<QuestionDraft> change, string field)
        {
            var e = PollDraft.FromDefinition(def);
            change(e.Questions[0]);
            Has(Fail(e, def), $"questions[0].{field}", "cannot be changed");
        }
        Changes(q => q.MinSelect = 2, "minSelect");
        Changes(q => q.MaxSelect = 3, "maxSelect");
        Changes(q => q.AllowOther = false, "allowOther");
        Changes(q => q.Display = ChoiceDisplay.Dropdown, "display");
    }

    [Fact]
    public void Choice_edit_keeps_enough_options_for_the_maximum()
    {
        var def = Stored(Choice(c => { c.MaxSelect = 3; }));
        var d = PollDraft.FromDefinition(def);
        d.Questions[0].Options.RemoveAt(0);
        Has(Fail(d, def), "questions[0].options", "at least 3");
    }

    [Fact]
    public void Text_and_number_rules_are_fixed()
    {
        var def = Stored(Text(t => { t.MinLength = 2; t.MaxLength = 40; }), Number(n => { n.Min = 1; n.Max = 9; n.Decimals = 2; n.Unit = "kg"; }));
        var same = PollDraft.FromDefinition(def);
        same.Questions[0].Prompt = "Renamed";
        Assert.Equal("Renamed", Ok(same, def).Questions[0].Prompt);

        void Changes(int q, Action<QuestionDraft> change, string field)
        {
            var e = PollDraft.FromDefinition(def);
            change(e.Questions[q]);
            Has(Fail(e, def), $"questions[{q}].{field}", "cannot be changed");
        }
        Changes(0, q => q.Multiline = true, "multiline");
        Changes(0, q => q.MinLength = 3, "minLength");
        Changes(0, q => q.MaxLength = 41, "maxLength");
        Changes(1, q => q.Min = 2, "min");
        Changes(1, q => q.Max = 10, "max");
        Changes(1, q => q.Decimals = 3, "decimals");
        Changes(1, q => q.Unit = "lb", "unit");
    }

    [Fact]
    public void A_stored_text_maximum_survives_a_lower_admin_limit()
    {
        var def = Stored(Text(t => t.MaxLength = 1500));
        var d = PollDraft.FromDefinition(def);
        d.Questions[0].MaxLength = null;
        Assert.Equal(1500, Ok(d, def, PollLimits.Default with { TextAnswerMax = 500 }).Questions[0].MaxLength);
    }

    [Fact]
    public void Old_drafts_without_the_new_fields_still_mean_the_same()
    {
        // Settings of other types stay in the draft and are ignored.
        var q = new QuestionDraft { Type = PollType.Ranked, Options = [O("A"), O("B")], MinSelect = 9, MaxSelect = 0, Decimals = 99, Unit = new string('x', 99) };
        Ok(Draft(q));
    }
}
