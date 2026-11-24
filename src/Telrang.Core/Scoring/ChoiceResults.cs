using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Core.Scoring;

/// <param name="Count">Respondents who picked this option.</param>
/// <param name="Share">Count / respondents (0 to 1); 0 when nobody answered.</param>
public sealed record ChoiceResultRow(string Key, string Label, int Count, double Share);

/// <param name="RespondentCount">Ballots that answered the question: at least one current option or Other. The share base.</param>
/// <param name="OtherCount">Respondents who picked Other (0 when the question has none). The texts are owner-only and not here.</param>
public sealed record ChoiceResultSet(int RespondentCount, int OtherCount, double OtherShare, IReadOnlyList<ChoiceResultRow> Rows);

public static class ChoiceResults
{
    /// <summary>
    /// Rows in the question's option order. Shares are of respondents, so with several selections they add up to more than 1.
    /// Keys of removed options are ignored (<see cref="BallotFilter"/>); a ballot left with nothing is no respondent.
    /// </summary>
    public static ChoiceResultSet Compute(PollQuestion poll, IEnumerable<ChoiceBallot> ballots)
    {
        var known = BallotFilter.For(poll);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var respondents = 0;
        var other = 0;
        foreach (var raw in ballots)
        {
            var b = known.Filter(raw);
            var isOther = b.Other && poll.AllowOther;
            if (b.Keys.Count == 0 && !isOther) continue;
            respondents++;
            if (isOther) other++;
            foreach (var k in b.Keys) counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        double Share(int n) => respondents == 0 ? 0 : (double)n / respondents;
        var rows = poll.Options.Select(o => new ChoiceResultRow(o.Key, o.Label, counts.GetValueOrDefault(o.Key), Share(counts.GetValueOrDefault(o.Key)))).ToList();
        return new ChoiceResultSet(respondents, other, Share(other), rows);
    }
}
