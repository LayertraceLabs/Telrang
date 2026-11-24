using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Core.Scoring;

/// <summary>A histogram bucket: values from <paramref name="From"/> up to, not including, <paramref name="To"/> (the last bucket includes it).</summary>
public sealed record NumberBucket(decimal From, decimal To, int Count);

/// <param name="Count">Answers.</param>
/// <param name="Median">The middle value; the mean of the two middle ones for an even count.</param>
/// <param name="Histogram">Empty without answers; one bucket when all answers are equal.</param>
public sealed record NumberResultSet(int Count, decimal? Min, decimal? Max, decimal? Mean, decimal? Median, IReadOnlyList<NumberBucket> Histogram);

public static class NumberResults
{
    /// <summary>The default number of histogram buckets.</summary>
    public const int DefaultBuckets = 10;

    /// <summary>
    /// Count, min, max, mean, median and a histogram over the answers' range in <paramref name="buckets"/> equal buckets. A whole-number
    /// question whose range has no more distinct values than buckets gets one bucket per value. Answers are assumed valid
    /// (<see cref="BallotValidator.ValidateNumber"/>: at most 10^15 each, so sums stay inside <see cref="decimal"/>).
    /// </summary>
    public static NumberResultSet Compute(PollQuestion poll, IEnumerable<NumberBallot> ballots, int buckets = DefaultBuckets)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(ballots);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);
        var values = ballots.Select(b => b.Value).Order().ToList();
        if (values.Count == 0) return new NumberResultSet(0, null, null, null, null, []);
        var lo = values[0];
        var hi = values[^1];
        var mid = values.Count / 2;
        var median = values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
        return new NumberResultSet(values.Count, lo, hi, values.Sum() / values.Count, median, Histogram(poll, values, lo, hi, buckets));
    }

    private static List<NumberBucket> Histogram(PollQuestion poll, List<decimal> sorted, decimal lo, decimal hi, int buckets)
    {
        if (lo == hi) return [new NumberBucket(lo, hi, sorted.Count)];
        if (poll.Decimals == 0 && hi - lo + 1 <= buckets)
        {
            var whole = new List<NumberBucket>();
            for (var v = lo; v <= hi; v++) whole.Add(new NumberBucket(v, v + 1, sorted.Count(x => x == v)));
            return whole;
        }
        var width = (hi - lo) / buckets;
        var counts = new int[buckets];
        foreach (var v in sorted) counts[Math.Min((int)((v - lo) / width), buckets - 1)]++;
        return Enumerable.Range(0, buckets)
            .Select(i => new NumberBucket(lo + i * width, i == buckets - 1 ? hi : lo + (i + 1) * width, counts[i])).ToList();
    }
}
