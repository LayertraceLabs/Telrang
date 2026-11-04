using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Core.Scoring;

/// <param name="RankHistogram">Index i = number of voters who ranked this option at rank i+1; unranked excluded.</param>
public sealed record RankedResultRow(string Key, string Label, double Score, int FirstChoiceCount, int BanCount, int[] RankHistogram);

public sealed record RankedResultSet(int VoterCount, IReadOnlyList<RankedResultRow> Rows);

public static class RankedResults
{
    /// <summary>
    /// Borda: rank r of N options gives N-r+1 points. Dowdall: rank r gives 1/r. Unranked = 0, bans never subtract.
    /// Order: Score desc, BanCount asc, Label. Scores are compared rounded to 9 decimals to avoid float noise.
    /// Ballots are assumed valid; unknown keys are ignored (the ranked options below one move up).
    /// </summary>
    public static RankedResultSet Compute(PollQuestion poll, IEnumerable<RankedBallot> ballots)
    {
        var n = poll.Options.Count;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++) index[poll.Options[i].Key] = i;
        var score = new double[n];
        var first = new int[n];
        var bans = new int[n];
        var hist = new int[n][];
        for (var i = 0; i < n; i++) hist[i] = new int[n];
        var voters = 0;
        var known = BallotFilter.For(poll); // once per computation, not per ballot

        foreach (var raw in ballots)
        {
            // Unknown keys (removed options, DESIGN.md §26) are dropped first, so the options below one move up.
            var b = known.Filter(raw);
            voters++;
            for (var r = 1; r <= b.Ranked.Count; r++)
            {
                if (!index.TryGetValue(b.Ranked[r - 1], out var i)) continue;
                score[i] += poll.Scoring == ScoringScheme.Dowdall ? 1.0 / r : n - r + 1;
                if (r == 1) first[i]++;
                if (r <= n) hist[i][r - 1]++;
            }
            foreach (var k in b.Banned.Distinct())
                if (index.TryGetValue(k, out var i)) bans[i]++;
        }

        var rows = poll.Options
            .Select((o, i) => new RankedResultRow(o.Key, o.Label, score[i], first[i], bans[i], hist[i]))
            .OrderByDescending(r => Math.Round(r.Score, 9))
            .ThenBy(r => r.BanCount)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Label, StringComparer.Ordinal)
            .ToList();
        return new RankedResultSet(voters, rows);
    }
}
