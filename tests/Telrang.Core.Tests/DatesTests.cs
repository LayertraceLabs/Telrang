using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Scoring;

namespace Telrang.Core.Tests;

/// <summary>Dates questions: the Text-mode line forms (<see cref="OptionListParser"/>) through the validator.</summary>
public class DatesDefinitionTests
{
    private static PollDraft Draft(IEnumerable<OptionDraft> options, bool maybe = true) => new()
    {
        Title = "T",
        Questions = [new QuestionDraft { Type = PollType.Dates, Maybe = maybe, Options = options.ToList() }],
    };

    private static string Text(string[] items) => string.Join("\n", items.Select(i => i.StartsWith(' ') ? i : "- " + i));

    private static PollQuestion Ok(string[] items, PollLimits? limits = null, bool maybe = true)
    {
        limits ??= PollLimits.Default;
        var parsed = OptionListParser.Parse(Text(items), PollType.Dates, limits);
        Assert.True(parsed.Success, string.Join("; ", parsed.Errors.Select(e => $"{e.Line}: {e.Message}")));
        var r = PollDraftValidator.Validate(Draft(parsed.Options, maybe), limits);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return r.Definition!.Questions[0];
    }

    private static PollQuestion Ok(params string[] items) => Ok(items, null);

    /// <summary>Errors of the text, else of the validator (as line 0).</summary>
    private static IReadOnlyList<OptionListError> Fail(string[] items, PollLimits? limits = null)
    {
        limits ??= PollLimits.Default;
        var parsed = OptionListParser.Parse(Text(items), PollType.Dates, limits);
        if (!parsed.Success) return parsed.Errors;
        var r = PollDraftValidator.Validate(Draft(parsed.Options), limits);
        Assert.False(r.Success);
        return r.Errors.Select(e => new OptionListError(0, e.Message)).ToList();
    }

    private static IReadOnlyList<OptionListError> Fail(params string[] items) => Fail(items, null);

    private static string[] Labels(PollQuestion d) => d.Options.Select(o => o.Label).ToArray();

    [Fact]
    public void Each_item_form()
    {
        var d = Ok("2026-11-03", "2026-11-04 19:00", "2026-11-05 18:30-21:00", "2026-11-06 to 2026-11-07");
        Assert.Equal(PollType.Dates, d.Type);
        Assert.Equal(["2026-11-03", "2026-11-04 19:00", "2026-11-05 18:30–21:00", "2026-11-06", "2026-11-07"], Labels(d));
        var o = d.Options[2];
        Assert.Equal(new DateOnly(2026, 11, 5), o.Date);
        Assert.Equal(new TimeOnly(18, 30), o.Start);
        Assert.Equal(new TimeOnly(21, 0), o.End);
        Assert.Null(d.Options[0].Start);
        Assert.Null(d.Options[0].End);
        Assert.Equal(new TimeOnly(19, 0), d.Options[1].Start);
        Assert.Null(d.Options[1].End);
    }

    [Fact]
    public void En_dash_span_is_accepted_and_normalised()
    {
        var d = Ok("2026-11-05 18:30–21:00", "2026-11-06");
        Assert.Equal("2026-11-05 18:30–21:00", d.Options[0].Label);
    }

    [Fact]
    public void Range_with_time_applies_to_each_day()
    {
        var d = Ok("2026-11-10 to 2026-11-12 19:00", "2026-11-20 to 2026-11-21 18:00-20:00");
        Assert.Equal(["2026-11-10 19:00", "2026-11-11 19:00", "2026-11-12 19:00", "2026-11-20 18:00–20:00", "2026-11-21 18:00–20:00"], Labels(d));
        Assert.All(d.Options.Take(3), o => Assert.Equal(new TimeOnly(19, 0), o.Start));
    }

    [Fact]
    public void Single_day_range_is_one_option()
    {
        Assert.Equal(["2026-11-03", "2026-11-04"], Labels(Ok("2026-11-03 to 2026-11-03", "2026-11-04")));
    }

    [Fact]
    public void Options_are_sorted_chronologically()
    {
        var d = Ok("2026-11-05", "2026-11-03 20:00", "2026-11-03", "2026-11-03 09:00-10:00", "2026-11-03 09:00");
        Assert.Equal(["2026-11-03", "2026-11-03 09:00", "2026-11-03 09:00–10:00", "2026-11-03 20:00", "2026-11-05"], Labels(d));
    }

    [Fact]
    public void Keys_are_slugs_of_the_normalised_label_and_stable()
    {
        var d = Ok("2026-11-05 18:30-21:00", "2026-11-03");
        Assert.Equal(["2026-11-03", "2026-11-05-18-30-21-00"], d.Options.Select(o => o.Key));
        Assert.Equal(PollRules.Slugify(d.Options[1].Label), d.Options[1].Key);
        var again = Ok("2026-11-03", "2026-11-05 18:30–21:00");
        Assert.Equal(d.Options.Select(o => o.Key), again.Options.Select(o => o.Key));
    }

    [Fact]
    public void Duplicates_are_errors_on_the_second_occurrence()
    {
        var e = Assert.Single(Fail("2026-11-03 19:00", "2026-11-04", "2026-11-03 19:00"));
        Assert.Equal(3, e.Line);
        Assert.Equal("'2026-11-03 19:00' is listed twice.", e.Message);
    }

    [Fact]
    public void Duplicate_through_a_range_is_an_error()
    {
        var e = Assert.Single(Fail("2026-11-04", "2026-11-03 to 2026-11-05"));
        Assert.Equal("'2026-11-04' is listed twice.", e.Message);
    }

    [Fact]
    public void Same_day_with_different_times_is_not_a_duplicate()
    {
        Ok("2026-11-03", "2026-11-03 19:00", "2026-11-03 19:00-20:00");
    }

    [Theory]
    [InlineData("Paris")]
    [InlineData("2026-11-3")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("2026-11-03 7:00")]
    [InlineData("2026-11-03 24:00")]
    [InlineData("2026-11-03 19:00 extra")]
    [InlineData("2026-11-03 to")]
    [InlineData("2026-11-03 to 2026-11-05 to 2026-11-06")]
    public void Bad_item_gets_the_help_text(string item)
    {
        var e = Assert.Single(Fail("2026-11-01", "2026-11-02", item));
        Assert.Equal(3, e.Line);
        Assert.Equal(PollRules.DateItemHelp, e.Message);
    }

    [Fact]
    public void End_before_start_is_a_line_error()
    {
        var time = Assert.Single(Fail("2026-11-01", "2026-11-09", "2026-11-02 21:00-18:00"));
        Assert.Equal(3, time.Line);
        var date = Assert.Single(Fail("2026-11-01", "2026-11-09", "2026-11-05 to 2026-11-02"));
        Assert.Equal(3, date.Line);
    }

    [Fact]
    public void Maybe_defaults_to_yes_and_is_read()
    {
        Assert.True(new QuestionDraft().Maybe);
        Assert.True(Ok(["2026-11-01", "2026-11-02"]).Maybe);
        Assert.False(Ok(["2026-11-01", "2026-11-02"], maybe: false).Maybe);
    }

    [Fact]
    public void Other_types_have_no_date_fields_and_default_maybe()
    {
        var r = PollDraftValidator.Validate(new PollDraft
        {
            Title = "T",
            Questions = [new QuestionDraft { Type = PollType.Ranked, Maybe = false, Options = [new() { Label = "2026-11-03", Date = new DateOnly(2026, 11, 3) }, new() { Label = "B" }] }],
        }, PollLimits.Default);
        var d = r.Definition!.Questions[0];
        Assert.All(d.Options, o => { Assert.Null(o.Date); Assert.Null(o.Start); Assert.Null(o.End); });
        Assert.Equal("2026-11-03", d.Options[0].Label);
        Assert.True(d.Maybe);
    }

    [Fact]
    public void Options_cap_counts_expanded_ranges()
    {
        var limits = new PollLimits(OptionsMax: 5);
        Ok(["2026-11-01 to 2026-11-05"], limits);
        var errors = Fail(["2026-11-01 to 2026-11-03", "2026-11-10 to 2026-11-13"], limits);
        Assert.Contains(errors, e => e.Message.StartsWith("Remove options: at most 5 are allowed (now 7)"));
    }

    [Fact]
    public void Huge_range_is_refused_without_expanding()
    {
        var errors = Fail("2026-11-01", "0001-01-01 to 9999-12-31");
        Assert.Contains(errors, e => e.Message.StartsWith("Remove options: at most 100 are allowed"));
    }

    [Fact]
    public void Single_date_is_too_few_options()
    {
        Assert.Contains(Fail("2026-11-01"), e => e.Message.StartsWith("Add at least 2 options"));
    }

    [Fact]
    public void Descriptions_under_date_items_work_and_copy_to_each_expanded_day()
    {
        var d = Ok("2026-11-03", "  Hall A, bring food", "2026-11-05 to 2026-11-06", "  Outside");
        Assert.Equal("Hall A, bring food", d.Options[0].DescriptionMarkdown);
        Assert.Equal("Outside", d.Options[1].DescriptionMarkdown);
        Assert.Equal("Outside", d.Options[2].DescriptionMarkdown);
    }

    [Fact]
    public void Edit_cannot_change_type_to_or_from_dates()
    {
        var dates = Draft([new() { Date = new DateOnly(2026, 11, 1) }, new() { Date = new DateOnly(2026, 11, 2) }]);
        var existing = PollDraftValidator.Validate(dates, PollLimits.Default).Definition!;
        var ranked = new PollDraft { Title = "T", Questions = [new QuestionDraft { Type = PollType.Ranked, Options = [new() { Label = "A" }, new() { Label = "B" }] }] };
        Assert.Contains(PollDraftValidator.Validate(ranked, PollLimits.Default, existing).Errors, e => e.Field == "questions[0].type");
        Assert.True(PollDraftValidator.Validate(dates, PollLimits.Default, existing).Success);
    }
}

public class DatesBallotValidatorTests
{
    private static PollQuestion Poll(bool maybe = true) =>
        new("q1", PollType.Dates, [new PollOption("a", "2026-11-03", ""), new PollOption("b", "2026-11-04", "")]) { Maybe = maybe };

    private static DatesBallot B(params (string, Availability)[] a) => new(a.ToDictionary(x => x.Item1, x => x.Item2));

    [Fact]
    public void Valid_ballot() =>
        Assert.Empty(BallotValidator.Validate(Poll(), B(("a", Availability.Yes), ("b", Availability.Maybe))));

    [Fact]
    public void Missing_keys_and_all_no_are_valid()
    {
        Assert.Empty(BallotValidator.Validate(Poll(), B()));
        Assert.Empty(BallotValidator.Validate(Poll(), B(("a", Availability.No), ("b", Availability.No))));
        Assert.Empty(BallotValidator.Validate(Poll(), B(("b", Availability.Yes))));
    }

    [Fact]
    public void Unknown_key() =>
        Assert.Equal(["Unknown option 'zz'."], BallotValidator.Validate(Poll(), B(("zz", Availability.Yes))));

    [Fact]
    public void Maybe_refused_when_disabled()
    {
        Assert.Equal(["Option 'a' cannot be 'maybe' in this poll."],
            BallotValidator.Validate(Poll(maybe: false), B(("a", Availability.Maybe))));
        Assert.Empty(BallotValidator.Validate(Poll(maybe: false), B(("a", Availability.Yes), ("b", Availability.No))));
    }

    [Fact]
    public void Undefined_enum_value() =>
        Assert.Single(BallotValidator.Validate(Poll(), B(("a", (Availability)9))));

    [Fact]
    public void Wrong_poll_type() =>
        Assert.Equal(["This question is not a dates question."], BallotValidator.Validate(TestData.Ranked(), B()));

    [Fact]
    public void Dates_poll_refuses_other_ballots()
    {
        Assert.Equal(["This question is not a ranked question."], BallotValidator.Validate(Poll(), new RankedBallot([], [])));
        Assert.Equal(["This question is not a points question."], BallotValidator.Validate(Poll(), new PointsBallot(new Dictionary<string, int>())));
    }
}

public class DatesResultsTests
{
    private static PollQuestion Poll(params string[] keys) => TestData.Dates(true, keys);

    private const Availability Y = Availability.Yes, M = Availability.Maybe, N = Availability.No;
    private static DatesBallot B(params (string, Availability)[] a) => new(a.ToDictionary(x => x.Item1, x => x.Item2));

    [Fact]
    public void Counts_and_no_derivation()
    {
        var rs = DatesResults.Compute(Poll("a", "b"), [B(("a", Y), ("b", M)), B(("a", Y)), B(("a", M), ("b", N)), B()]);
        Assert.Equal(4, rs.VoterCount);
        Assert.Equal((2, 1, 1), (rs.Rows[0].Yes, rs.Rows[0].Maybe, rs.Rows[0].No));
        Assert.Equal((0, 1, 3), (rs.Rows[1].Yes, rs.Rows[1].Maybe, rs.Rows[1].No));
    }

    [Fact]
    public void Rows_keep_option_order()
    {
        var rs = DatesResults.Compute(Poll("c", "a", "b"), [B(("b", Y))]);
        Assert.Equal(["c", "a", "b"], rs.Rows.Select(r => r.Key));
        Assert.Equal(["C", "A", "B"], rs.Rows.Select(r => r.Label));
    }

    [Fact]
    public void Places_use_yes_then_maybe_with_shared_places()
    {
        // a: 2y0m, b: 1y2m, c: 1y2m, d: 1y0m, e: 0y0m
        var ballots = new[]
        {
            B(("a", Y), ("b", Y), ("c", M), ("d", Y)),
            B(("a", Y), ("b", M), ("c", Y)),
            B(("b", M), ("c", M)),
        };
        var rs = DatesResults.Compute(Poll("e", "d", "c", "b", "a"), ballots);
        var place = rs.Rows.ToDictionary(r => r.Key, r => r.Place);
        Assert.Equal(1, place["a"]);
        Assert.Equal(2, place["b"]);
        Assert.Equal(2, place["c"]);
        Assert.Equal(4, place["d"]);
        Assert.Equal(5, place["e"]);
    }

    [Fact]
    public void Ties_on_yes_break_by_maybe()
    {
        var rs = DatesResults.Compute(Poll("a", "b"), [B(("a", Y), ("b", Y)), B(("b", M))]);
        Assert.Equal([2, 1], rs.Rows.Select(r => r.Place));
    }

    [Fact]
    public void No_ballots_all_share_first_place()
    {
        var rs = DatesResults.Compute(Poll("a", "b"), []);
        Assert.Equal(0, rs.VoterCount);
        Assert.All(rs.Rows, r => { Assert.Equal(1, r.Place); Assert.Equal(0, r.No); });
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var rs = DatesResults.Compute(Poll("a"), [B(("a", Y), ("zz", Y))]);
        Assert.Single(rs.Rows);
        Assert.Equal(1, rs.Rows[0].Yes);
        Assert.Equal(1, rs.VoterCount);
    }
}
