using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

internal static class TestData
{
    public static PollQuestion Ranked(ScoringScheme scoring = ScoringScheme.Borda, int maxBans = 0, bool rankAll = false, params string[] labels)
    {
        if (labels.Length == 0) labels = ["A", "B", "C"];
        return new PollQuestion("q1", PollType.Ranked, labels.Select(l => new PollOption(l.ToLowerInvariant(), l, "")).ToList())
        {
            Scoring = scoring,
            MaxBans = maxBans,
            RankAll = rankAll,
        };
    }

    public static PollQuestion Points(int budget = 10, int? maxPer = null, bool spendAll = false, params string[] labels)
    {
        if (labels.Length == 0) labels = ["A", "B", "C"];
        return new PollQuestion("q1", PollType.Points, labels.Select(l => new PollOption(l.ToLowerInvariant(), l, "")).ToList())
        {
            Budget = budget,
            MaxPerOption = maxPer,
            SpendAll = spendAll,
        };
    }

    public static PollQuestion Dates(bool maybe = true, params string[] keys) =>
        new("q1", PollType.Dates, keys.Select(k => new PollOption(k, k.ToUpperInvariant(), "")).ToList()) { Maybe = maybe };
}
