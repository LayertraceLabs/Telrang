using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

/// <summary>DESIGN.md §34.3: Choice, Text and Number answers, "Required" for every type, free text kept apart.</summary>
public class NewTypeBallotTests
{
    private static PollQuestion Choice(int min = 1, int max = 1, bool other = false, bool required = false) =>
        new("q1", PollType.Choice, [new PollOption("a", "A", ""), new PollOption("b", "B", ""), new PollOption("c", "C", "")])
        { MinSelect = min, MaxSelect = max, AllowOther = other, Required = required };

    private static PollQuestion Text(bool required = false, int? min = null, int? max = 20, bool multiline = false) =>
        new("q1", PollType.Text, []) { Required = required, MinLength = min, MaxLength = max, Multiline = multiline };

    private static PollQuestion Number(decimal? min = null, decimal? max = null, int decimals = 0, bool required = false, string unit = "") =>
        new("q1", PollType.Number, []) { Min = min, Max = max, Decimals = decimals, Required = required, Unit = unit };

    private static ChoiceBallot C(bool other, params string[] keys) => new(keys, other);

    // ---- Choice ----

    [Fact]
    public void Choice_one_option_is_valid()
        => Assert.Empty(BallotValidator.Validate(Choice(), C(false, "a")));

    [Fact]
    public void Choice_empty_is_no_answer_unless_required()
    {
        Assert.Empty(BallotValidator.Validate(Choice(), C(false)));
        Assert.Equal(["Pick an option."], BallotValidator.Validate(Choice(required: true), C(false)));
        Assert.Equal(["Pick at least 2."], BallotValidator.Validate(Choice(2, 3, required: true), C(false)));
    }

    [Fact]
    public void Choice_selection_counts()
    {
        Assert.Equal(["Pick one option."], BallotValidator.Validate(Choice(), C(false, "a", "b")));
        Assert.Equal(["Pick at least 2."], BallotValidator.Validate(Choice(2, 3), C(false, "a")));
        Assert.Equal(["Pick at most 2."], BallotValidator.Validate(Choice(1, 2), C(false, "a", "b", "c")));
        Assert.Empty(BallotValidator.Validate(Choice(2, 3), C(false, "a", "c")));
    }

    [Fact]
    public void Choice_unknown_and_repeated_keys()
    {
        Assert.Contains(BallotValidator.Validate(Choice(), C(false, "zzz")), e => e.Contains("Unknown option 'zzz'"));
        Assert.Contains(BallotValidator.Validate(Choice(1, 3), C(false, "a", "a")), e => e.Contains("more than once"));
    }

    [Fact]
    public void Choice_other_counts_as_a_selection_and_needs_text()
    {
        var q = Choice(1, 2, other: true);
        Assert.Empty(BallotValidator.Validate(q, C(true, "a"), "Purple"));
        Assert.Equal(["Pick at most 2."], BallotValidator.Validate(q, C(true, "a", "b"), "Purple"));
        Assert.Empty(BallotValidator.Validate(q, C(true), "Purple"));
        Assert.Equal(["Enter your Other answer."], BallotValidator.Validate(q, C(true, "a"), "  "));
        Assert.Equal(["Enter your Other answer."], BallotValidator.Validate(q, C(true, "a")));
        Assert.Equal(["Remove the Other text, or pick Other."], BallotValidator.Validate(q, C(false, "a"), "Purple"));
    }

    [Fact]
    public void Choice_other_only_where_offered_and_within_the_text_limit()
    {
        Assert.Equal(["This question has no Other option."], BallotValidator.Validate(Choice(), C(true), "x"));
        var q = Choice(other: true);
        Assert.Empty(BallotValidator.Validate(q, C(true), new string('x', 2000)));
        Assert.Single(BallotValidator.Validate(q, C(true), new string('x', 2001)));
        Assert.Single(BallotValidator.Validate(q, C(true), "abcdef", PollLimits.Default with { TextAnswerMax = 5 }));
    }

    [Fact]
    public void Choice_validation_refuses_other_types()
        => Assert.Single(BallotValidator.Validate(TestData.Ranked(), C(false, "a")));

    // ---- Text ----

    [Fact]
    public void Text_valid_blank_and_required()
    {
        Assert.Empty(BallotValidator.ValidateText(Text(), "hello"));
        Assert.Empty(BallotValidator.ValidateText(Text(), null));
        Assert.Empty(BallotValidator.ValidateText(Text(), "   "));
        Assert.Equal(["Enter an answer."], BallotValidator.ValidateText(Text(required: true), "  \n "));
        Assert.Equal(["Enter an answer."], BallotValidator.ValidateText(Text(required: true), null));
    }

    [Fact]
    public void Text_length_limits_count_the_trimmed_text()
    {
        Assert.Empty(BallotValidator.ValidateText(Text(max: 5), "  abcde  "));
        Assert.Equal(["Shorten the answer to 5 characters (now 6)."], BallotValidator.ValidateText(Text(max: 5), "abcdef"));
        Assert.Equal(["Write at least 3 characters (now 2)."], BallotValidator.ValidateText(Text(min: 3), "ab"));
        Assert.Empty(BallotValidator.ValidateText(Text(min: 3), "abc"));
        // No stored maximum: the admin limit applies.
        Assert.Single(BallotValidator.ValidateText(Text(max: null), new string('x', 11), PollLimits.Default with { TextAnswerMax = 10 }));
    }

    [Fact]
    public void Text_single_line_refuses_line_breaks()
    {
        Assert.Equal(["Use a single line."], BallotValidator.ValidateText(Text(), "a\nb"));
        Assert.Empty(BallotValidator.ValidateText(Text(multiline: true), "a\nb"));
        Assert.Empty(BallotValidator.ValidateText(Text(), "a\n")); // the trailing break is trimmed
    }

    [Fact]
    public void Text_validation_refuses_other_types()
        => Assert.Single(BallotValidator.ValidateText(TestData.Ranked(), "x"));

    // ---- Number ----

    [Theory]
    [InlineData("12", "12")]
    [InlineData("-3.5", "-3.5")]
    [InlineData("+7", "7")]
    [InlineData("12.50", "12.5")]
    [InlineData(" 0.25 ", "0.25")]
    [InlineData(".5", "0.5")]
    public void Number_parses_invariant(string text, string stored)
    {
        Assert.True(NumberBallot.TryParse(text, Number(decimals: 2), out var b, out var error), error);
        Assert.Equal(stored, b!.ToInvariantString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,5")]
    [InlineData("1e3")]
    [InlineData("1 000")]
    [InlineData("1.2.3")]
    [InlineData("--1")]
    [InlineData("NaN")]
    public void Number_refuses_what_is_not_a_plain_number(string text)
    {
        Assert.False(NumberBallot.TryParse(text, Number(decimals: 2), out var b, out var error));
        Assert.Null(b);
        Assert.Equal("Enter a number such as 12.5.", error);
    }

    [Fact]
    public void Number_empty_is_no_answer()
    {
        Assert.True(NumberBallot.TryParse("  ", Number(), out var b, out var error));
        Assert.Null(b);
        Assert.Null(error);
    }

    [Fact]
    public void Number_with_too_many_decimals_is_refused_never_rounded()
    {
        Assert.False(NumberBallot.TryParse("1.234", Number(decimals: 2), out var b, out var error));
        Assert.Null(b);
        Assert.Equal("Use at most 2 decimal places.", error);
        Assert.Equal("Use a whole number.", NumberBallot.TryParse("1.5", Number(), out _, out var e2) ? null : e2);
        Assert.True(NumberBallot.TryParse("1.230", Number(decimals: 2), out _, out _)); // the same value
        Assert.True(NumberBallot.TryParse("3.0", Number(), out var whole, out _));
        Assert.Equal(3m, whole!.Value);
        Assert.Equal("Use at most 6 decimal places.", BallotValidator.ValidateNumber(Number(decimals: 6), new NumberBallot(0.1234567m)).Single());
    }

    [Fact]
    public void Number_bounds_and_the_absolute_limit()
    {
        var q = Number(min: 1, max: 10, decimals: 1, unit: "kg");
        Assert.Empty(BallotValidator.ValidateNumber(q, new NumberBallot(1)));
        Assert.Empty(BallotValidator.ValidateNumber(q, new NumberBallot(10)));
        Assert.Equal(["Enter 1 kg or more."], BallotValidator.ValidateNumber(q, new NumberBallot(0.9m)));
        Assert.Equal(["Enter 10 kg or less."], BallotValidator.ValidateNumber(q, new NumberBallot(10.1m)));
        Assert.Single(BallotValidator.ValidateNumber(Number(), new NumberBallot(1e16m)));
        Assert.Empty(BallotValidator.ValidateNumber(Number(), new NumberBallot(-1e15m)));
    }

    [Fact]
    public void Number_required_and_other_types()
    {
        Assert.Empty(BallotValidator.ValidateNumber(Number(), null));
        Assert.Equal(["Enter a number."], BallotValidator.ValidateNumber(Number(required: true), null));
        Assert.Single(BallotValidator.ValidateNumber(TestData.Ranked(), new NumberBallot(1)));
    }

    [Fact]
    public void Number_ballot_formats_without_trailing_zeros()
    {
        Assert.Equal("100", new NumberBallot(100m).ToInvariantString());
        Assert.Equal("0", new NumberBallot(0.00m).ToInvariantString());
        Assert.Equal("-0.5", new NumberBallot(-0.50m).ToInvariantString());
    }

    // ---- Required, every type ----

    [Fact]
    public void Required_ranked_needs_a_ranked_option()
    {
        var q = TestData.Ranked(maxBans: 1) with { Required = true };
        Assert.Equal(["Rank at least one option."], BallotValidator.Validate(q, new RankedBallot([], [])));
        Assert.Equal(["Rank at least one option."], BallotValidator.Validate(q, new RankedBallot([], ["c"])));
        Assert.Empty(BallotValidator.Validate(q, new RankedBallot(["a"], [])));
        Assert.Empty(BallotValidator.Validate(TestData.Ranked(), new RankedBallot([], []))); // not required: as before
    }

    [Fact]
    public void Required_points_needs_more_than_zero_points()
    {
        var q = TestData.Points() with { Required = true };
        Assert.Equal(["Give at least one option points."], BallotValidator.Validate(q, new PointsBallot(new Dictionary<string, int>())));
        Assert.Single(BallotValidator.Validate(q, new PointsBallot(new Dictionary<string, int> { ["a"] = 0 })));
        Assert.Empty(BallotValidator.Validate(q, new PointsBallot(new Dictionary<string, int> { ["a"] = 1 })));
        Assert.Empty(BallotValidator.Validate(TestData.Points(), new PointsBallot(new Dictionary<string, int>())));
    }

    [Fact]
    public void Required_dates_needs_a_yes_or_maybe()
    {
        var q = TestData.Dates(true, "a", "b") with { Required = true };
        Assert.Single(BallotValidator.Validate(q, new DatesBallot(new Dictionary<string, Availability>())));
        Assert.Single(BallotValidator.Validate(q, new DatesBallot(new Dictionary<string, Availability> { ["a"] = Availability.No })));
        Assert.Empty(BallotValidator.Validate(q, new DatesBallot(new Dictionary<string, Availability> { ["a"] = Availability.Maybe })));
        Assert.Empty(BallotValidator.Validate(TestData.Dates(true, "a", "b"), new DatesBallot(new Dictionary<string, Availability>())));
    }

    // ---- BallotFilter ----

    [Fact]
    public void Filter_drops_removed_choice_keys_and_keeps_other()
    {
        var q = Choice(1, 3, other: true);
        var f = BallotFilter.Known(q, new ChoiceBallot(["a", "gone", "a", "c"], true));
        Assert.Equal(["a", "c"], f.Keys);
        Assert.True(f.Other);
        Assert.Empty(BallotFilter.Known(q, new ChoiceBallot(["gone"], false)).Keys);
    }

    // ---- free text, apart from the payload ----

    [Fact]
    public void Response_text_keys_name_text_questions_and_other()
    {
        var qs = new[] { Choice(other: true) with { Key = "q1" }, Text() with { Key = "q2" }, Choice() with { Key = "q3" } };
        Assert.Equal("q2", ResponseText.AnswerKey(qs[1]));
        Assert.Equal("q1.other", ResponseText.OtherKey(qs[0]));
        Assert.Empty(ResponseText.Validate(qs, new Dictionary<string, string> { ["q2"] = "a", ["q1.other"] = "b" }));
        var errors = ResponseText.Validate(qs, new Dictionary<string, string> { ["q1"] = "a", ["q3.other"] = "b", ["q9"] = "c" });
        Assert.Equal(["Unknown text answer 'q1'.", "Unknown text answer 'q3.other'.", "Unknown text answer 'q9'."], errors);
    }

    [Fact]
    public void Response_text_total_is_counted_in_utf8_bytes()
    {
        var qs = new[] { Text(max: 2000) with { Key = "q1" }, Text(max: 2000) with { Key = "q2" } };
        Assert.Equal(3 + 4, ResponseText.Bytes(new Dictionary<string, string> { ["q1"] = "€", ["q2"] = "😀" }));
        var atLimit = new Dictionary<string, string> { ["q1"] = new string('a', 4096), ["q2"] = new string('b', 4096) };
        Assert.Empty(ResponseText.Validate(qs, atLimit));
        atLimit["q2"] += "b";
        Assert.Equal(["Shorten your text answers to 8192 bytes in total (now 8193)."], ResponseText.Validate(qs, atLimit));
        // Two-byte characters: 4096 of them are 8192 bytes alone.
        Assert.Empty(ResponseText.Validate(qs, new Dictionary<string, string> { ["q1"] = new string('é', 4096) }));
        Assert.Single(ResponseText.Validate(qs, new Dictionary<string, string> { ["q1"] = new string('é', 4097) }));
    }

    [Fact]
    public void Response_text_possible_size_is_capped_by_the_limit()
    {
        Assert.Equal(0, ResponseText.MaxPossibleBytes([Choice(), TestData.Ranked()]));
        Assert.Equal(300, ResponseText.MaxPossibleBytes([Text(max: 100) with { Key = "q1" }]));
        Assert.Equal(8192, ResponseText.MaxPossibleBytes([Text(max: 2000), Text(max: 2000), Text(max: 2000)]));
        Assert.Equal(6000, ResponseText.MaxPossibleBytes([Choice(other: true)]));
    }
}
