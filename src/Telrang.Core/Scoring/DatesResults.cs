using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Core.Scoring;

/// <param name="No">Voter count minus yes minus maybe (missing answers read as no).</param>
/// <param name="Place">1 = most yes, ties by most maybe; equal (yes, maybe) share a place, the next place skips.</param>
public sealed record DatesResultRow(string Key, string Label, int Yes, int Maybe, int No, int Place);

public sealed record DatesResultSet(int VoterCount, IReadOnlyList<DatesResultRow> Rows);

public static class DatesResults
{
    /// <summary>Rows in the poll's option order. Unknown keys in ballots are ignored.</summary>
    public static DatesResultSet Compute(PollQuestion poll, IEnumerable<DatesBallot> ballots)
    {
        var list = ballots as IReadOnlyCollection<DatesBallot> ?? ballots.ToList();
        var counts = poll.Options.Select(o =>
        {
            int yes = 0, maybe = 0;
            foreach (var b in list)
            {
                if (!b.Answers.TryGetValue(o.Key, out var a)) continue;
                if (a == Availability.Yes) yes++;
                else if (a == Availability.Maybe) maybe++;
            }
            return (o, yes, maybe);
        }).ToList();

        var rows = counts.Select(c =>
        {
            var place = 1 + counts.Count(x => x.yes > c.yes || (x.yes == c.yes && x.maybe > c.maybe));
            return new DatesResultRow(c.o.Key, c.o.Label, c.yes, c.maybe, list.Count - c.yes - c.maybe, place);
        }).ToList();
        return new DatesResultSet(list.Count, rows);
    }
}
