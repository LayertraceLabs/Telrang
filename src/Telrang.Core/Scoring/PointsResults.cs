using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Core.Scoring;

/// <param name="Mean">Total / voter count (zeros included); 0 when there are no voters.</param>
/// <param name="SupporterCount">Voters who gave this option more than 0.</param>
/// <param name="Max">Highest single-voter value.</param>
public sealed record PointsResultRow(string Key, string Label, long Total, double Mean, int SupporterCount, int Max);

public sealed record PointsResultSet(int VoterCount, IReadOnlyList<PointsResultRow> Rows);

public static class PointsResults
{
    /// <summary>Order: Total desc, then Label. Options missing from a ballot count as 0.</summary>
    public static PointsResultSet Compute(PollQuestion poll, IEnumerable<PointsBallot> ballots)
    {
        var list = ballots as IReadOnlyCollection<PointsBallot> ?? ballots.ToList();
        var rows = poll.Options.Select(o =>
        {
            long total = 0; var supporters = 0; var max = 0;
            foreach (var b in list)
            {
                var v = b.Points.TryGetValue(o.Key, out var x) ? x : 0;
                total += v;
                if (v > 0) supporters++;
                if (v > max) max = v;
            }
            return new PointsResultRow(o.Key, o.Label, total, list.Count == 0 ? 0 : (double)total / list.Count, supporters, max);
        })
        .OrderByDescending(r => r.Total)
        .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Label, StringComparer.Ordinal)
        .ToList();
        return new PointsResultSet(list.Count, rows);
    }

    /// <summary>Value -> number of voters who gave this option exactly that value (zeros included), ascending by value.</summary>
    public static IReadOnlyDictionary<int, int> Histogram(IEnumerable<PointsBallot> ballots, string key)
    {
        var d = new SortedDictionary<int, int>();
        foreach (var b in ballots)
        {
            var v = b.Points.TryGetValue(key, out var x) ? x : 0;
            d[v] = d.GetValueOrDefault(v) + 1;
        }
        return d;
    }
}
