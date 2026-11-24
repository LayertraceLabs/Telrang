using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Scoring;

namespace Telrang.Core.Tests;

/// <summary>DESIGN.md §34.3: results of Choice, Number and Text questions.</summary>
public class NewTypeResultsTests
{
    private static PollQuestion Choice(bool other = false, int max = 3) =>
        new("q1", PollType.Choice, [new PollOption("a", "A", ""), new PollOption("b", "B", ""), new PollOption("c", "C", "")])
        { MaxSelect = max, AllowOther = other };

    private static ChoiceBallot C(params string[] keys) => new(keys, false);

    private static PollQuestion Number(int decimals = 0) => new("q1", PollType.Number, []) { Decimals = decimals };

    private static IEnumerable<NumberBallot> N(params decimal[] values) => values.Select(v => new NumberBallot(v));

    // ---- Choice ----

    [Fact]
    public void Choice_counts_and_shares_of_respondents_in_option_order()
    {
        var r = ChoiceResults.Compute(Choice(), [C("a"), C("a", "b"), C("b"), C("a", "c")]);
        Assert.Equal(4, r.RespondentCount);
        Assert.Equal(["a", "b", "c"], r.Rows.Select(x => x.Key));
        Assert.Equal([3, 2, 1], r.Rows.Select(x => x.Count));
        Assert.Equal([0.75, 0.5, 0.25], r.Rows.Select(x => x.Share));
        Assert.Equal(0, r.OtherCount);
    }

    [Fact]
    public void Choice_other_is_counted_when_offered()
    {
        var q = Choice(other: true);
        var r = ChoiceResults.Compute(q, [new ChoiceBallot(["a"], true), new ChoiceBallot([], true), C("b"), C("b")]);
        Assert.Equal((4, 2, 0.5), (r.RespondentCount, r.OtherCount, r.OtherShare));
        Assert.Equal([1, 2, 0], r.Rows.Select(x => x.Count));
        // A question without Other ignores the flag, and a ballot with nothing else is no respondent.
        var none = ChoiceResults.Compute(Choice(), [new ChoiceBallot([], true)]);
        Assert.Equal((0, 0), (none.RespondentCount, none.OtherCount));
    }

    [Fact]
    public void Choice_ignores_removed_options_and_empty_ballots()
    {
        var r = ChoiceResults.Compute(Choice(), [C("a", "gone"), C("gone"), C(), C("a", "a")]);
        Assert.Equal(2, r.RespondentCount);
        Assert.Equal([2, 0, 0], r.Rows.Select(x => x.Count));
        Assert.Equal(1.0, r.Rows[0].Share);
    }

    [Fact]
    public void Choice_without_ballots_is_all_zero()
    {
        var r = ChoiceResults.Compute(Choice(other: true), []);
        Assert.Equal(0, r.RespondentCount);
        Assert.All(r.Rows, x => Assert.Equal((0, 0.0), (x.Count, x.Share)));
        Assert.Equal(0.0, r.OtherShare);
    }

    // ---- Number ----

    [Fact]
    public void Number_without_answers()
    {
        var r = NumberResults.Compute(Number(), []);
        Assert.Equal((0, null, null, null, null), (r.Count, r.Min, r.Max, r.Mean, r.Median));
        Assert.Empty(r.Histogram);
    }

    [Fact]
    public void Number_statistics()
    {
        var r = NumberResults.Compute(Number(1), N(4, 1, 9, 2.5m));
        Assert.Equal((4, 1m, 9m, 4.125m, 3.25m), (r.Count, r.Min, r.Max, r.Mean, r.Median));
        Assert.Equal(4m, NumberResults.Compute(Number(), N(7, 1, 4)).Median);
        Assert.Equal(-2m, NumberResults.Compute(Number(), N(-2)).Median);
    }

    [Fact]
    public void Number_whole_values_get_one_bucket_each_when_few()
    {
        var r = NumberResults.Compute(Number(), N(1, 1, 3, 5, 5, 5));
        Assert.Equal([1m, 2m, 3m, 4m, 5m], r.Histogram.Select(b => b.From));
        Assert.Equal([2, 0, 1, 0, 3], r.Histogram.Select(b => b.Count));
        Assert.Equal(r.Count, r.Histogram.Sum(b => b.Count));
    }

    [Fact]
    public void Number_wide_ranges_use_equal_buckets_and_the_last_includes_the_maximum()
    {
        var r = NumberResults.Compute(Number(), N(0, 5, 10, 55, 99, 100), 10);
        Assert.Equal(10, r.Histogram.Count);
        Assert.Equal((0m, 100m), (r.Histogram[0].From, r.Histogram[^1].To));
        Assert.Equal([2, 1, 0, 0, 0, 1, 0, 0, 0, 2], r.Histogram.Select(b => b.Count));
        Assert.Equal(r.Count, r.Histogram.Sum(b => b.Count));
        Assert.All(r.Histogram.Zip(r.Histogram.Skip(1)), p => Assert.Equal(p.First.To, p.Second.From));
    }

    [Fact]
    public void Number_decimals_use_equal_buckets_and_equal_values_one_bucket()
    {
        var r = NumberResults.Compute(Number(1), N(0.5m, 1.5m, 2.5m), 3);
        Assert.Equal([1, 1, 1], r.Histogram.Select(b => b.Count));
        var same = NumberResults.Compute(Number(1), N(2.5m, 2.5m));
        Assert.Equal([new NumberBucket(2.5m, 2.5m, 2)], same.Histogram);
    }

    [Fact]
    public void Number_buckets_must_be_positive_and_large_values_do_not_overflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NumberResults.Compute(Number(), N(1), 0));
        var big = NumberResults.Compute(Number(6), Enumerable.Repeat(new NumberBallot(1e15m), 1000).Append(new NumberBallot(-1e15m)));
        Assert.Equal(1001, big.Count);
        Assert.Equal(1001, big.Histogram.Sum(b => b.Count));
    }

    // ---- Text ----

    [Fact]
    public void Text_counts_non_blank_answers_only()
    {
        var q = new PollQuestion("q1", PollType.Text, []);
        Assert.Equal(2, TextResults.Compute(q, ["a", "", null, "  ", "b"]).AnswerCount);
        Assert.Equal(0, TextResults.Compute(q, []).AnswerCount);
    }
}
