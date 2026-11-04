using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Scoring;

namespace Telrang.Core.Tests;

public class ScoringTests
{
    private static RankedBallot R(string[] ranked, params string[] banned) => new(ranked, banned);
    private static PointsBallot P(params (string, int)[] p) => new(p.ToDictionary(x => x.Item1, x => x.Item2));

    // Ballots (options A,B,C):
    //   1: A > B > C
    //   2: B > A, C unranked
    //   3: A, C banned
    private static readonly RankedBallot[] Ballots = [R(["a", "b", "c"]), R(["b", "a"]), R(["a"], "c")];

    [Fact]
    public void Borda_HandComputed()
    {
        // N=3: rank1=3, rank2=2, rank3=1.
        // A = 3 + 2 + 3 = 8 ; B = 2 + 3 + 0 = 5 ; C = 1 + 0 + 0 = 1
        var rs = RankedResults.Compute(TestData.Ranked(ScoringScheme.Borda, maxBans: 1), Ballots);
        Assert.Equal(3, rs.VoterCount);
        Assert.Equal(["A", "B", "C"], rs.Rows.Select(r => r.Label));
        Assert.Equal([8.0, 5.0, 1.0], rs.Rows.Select(r => r.Score));
        Assert.Equal([2, 1, 0], rs.Rows.Select(r => r.FirstChoiceCount));
        Assert.Equal([0, 0, 1], rs.Rows.Select(r => r.BanCount));
        // Rank histograms (index = rank-1, unranked excluded)
        Assert.Equal([2, 1, 0], rs.Rows[0].RankHistogram); // A: 1st twice, 2nd once
        Assert.Equal([1, 1, 0], rs.Rows[1].RankHistogram); // B: 1st once, 2nd once
        Assert.Equal([0, 0, 1], rs.Rows[2].RankHistogram); // C: 3rd once; banned/unranked excluded
    }

    [Fact]
    public void Dowdall_HandComputed()
    {
        // A = 1 + 1/2 + 1 = 2.5 ; B = 1/2 + 1 + 0 = 1.5 ; C = 1/3
        var rs = RankedResults.Compute(TestData.Ranked(ScoringScheme.Dowdall, maxBans: 1), Ballots);
        Assert.Equal(["A", "B", "C"], rs.Rows.Select(r => r.Label));
        Assert.Equal(2.5, rs.Rows[0].Score, 9);
        Assert.Equal(1.5, rs.Rows[1].Score, 9);
        Assert.Equal(1.0 / 3, rs.Rows[2].Score, 9);
    }

    [Fact]
    public void Borda_ScoreDependsOnOptionCount()
    {
        // N=4, single ballot ranking only X: X gets 4 points.
        var poll = TestData.Ranked(ScoringScheme.Borda, 0, false, "W", "X", "Y", "Z");
        var rs = RankedResults.Compute(poll, [R(["x"])]);
        Assert.Equal(4.0, rs.Rows.Single(r => r.Label == "X").Score);
        Assert.Equal(0.0, rs.Rows.Single(r => r.Label == "W").Score);
    }

    [Fact]
    public void Bans_NeverSubtract()
    {
        var poll = TestData.Ranked(ScoringScheme.Borda, 1);
        var rs = RankedResults.Compute(poll, [R(["a"], "b"), R(["a"], "b")]);
        var b = rs.Rows.Single(r => r.Label == "B");
        Assert.Equal(0.0, b.Score);
        Assert.Equal(2, b.BanCount);
    }

    [Fact]
    public void NoBallots_AllZero_OrderedByLabel()
    {
        var rs = RankedResults.Compute(TestData.Ranked(labels: ["Cherry", "Apple", "Banana"]), []);
        Assert.Equal(0, rs.VoterCount);
        Assert.Equal(["Apple", "Banana", "Cherry"], rs.Rows.Select(r => r.Label));
        Assert.All(rs.Rows, r => Assert.Equal(0.0, r.Score));
    }

    [Fact]
    public void Ranked_TieBreak_ByBansThenLabel()
    {
        // Borda N=3; each of Apple, Banana, Cherry gets exactly 3 (one first place each).
        // Banana is banned once, so it sorts after the other two; Apple < Cherry by label.
        var poll = TestData.Ranked(ScoringScheme.Borda, 1, false, "Cherry", "Banana", "Apple");
        var rs = RankedResults.Compute(poll, [R(["apple"], "banana"), R(["banana"]), R(["cherry"])]);
        Assert.Equal([3.0, 3.0, 3.0], rs.Rows.Select(r => r.Score));
        Assert.Equal(["Apple", "Cherry", "Banana"], rs.Rows.Select(r => r.Label));
    }

    [Fact]
    public void Ranked_ScoreBeatsBans()
    {
        var poll = TestData.Ranked(ScoringScheme.Borda, 1, false, "A", "B");
        // B: 2 + 2 = 4 with 0 bans? give A a ban but B higher.
        var rs = RankedResults.Compute(poll, [R(["b", "a"]), R(["b"], "a")]);
        Assert.Equal("B", rs.Rows[0].Label);
    }

    [Fact]
    public void Dowdall_FloatingPointTiesAreTies()
    {
        // X: 1/2 + 1/3 + 1/6?? use ranks: both options accumulate the same multiset of terms in different order.
        // Ballots (4 options W,X,Y,Z): [x,y,..] and [y,x,..] -> X = 1 + 1/2, Y = 1/2 + 1 : equal.
        var poll = TestData.Ranked(ScoringScheme.Dowdall, 0, false, "Y", "X", "W", "Z");
        var rs = RankedResults.Compute(poll, [R(["x", "y"]), R(["y", "x"])]);
        Assert.Equal(["X", "Y", "W", "Z"], rs.Rows.Select(r => r.Label));
    }

    // ---- points ----
    // Budget 10, options A,B,C, ballots:
    //   1: A=6 B=4        2: A=0 B=5 C=5      3: A=6 B=0 C=0 (C omitted)  4: A=2 B=1 C=7
    // Totals: A=14, B=10, C=12. Mean over 4 voters: 3.5, 2.5, 3.0.
    // Supporters (>0): A=3 (ballots 1,3,4), B=3 (1,2,4), C=2 (2,4). Max: A=6, B=5, C=7.
    private static readonly PointsBallot[] PBallots =
    [
        P(("a", 6), ("b", 4)),
        P(("a", 0), ("b", 5), ("c", 5)),
        P(("a", 6), ("b", 0)),
        P(("a", 2), ("b", 1), ("c", 7)),
    ];

    [Fact]
    public void Points_HandComputed()
    {
        var rs = PointsResults.Compute(TestData.Points(10), PBallots);
        Assert.Equal(4, rs.VoterCount);
        Assert.Equal(["A", "C", "B"], rs.Rows.Select(r => r.Label));
        Assert.Equal([14L, 12L, 10L], rs.Rows.Select(r => r.Total));
        Assert.Equal([3.5, 3.0, 2.5], rs.Rows.Select(r => r.Mean));
        Assert.Equal([3, 2, 3], rs.Rows.Select(r => r.SupporterCount));
        Assert.Equal([6, 7, 5], rs.Rows.Select(r => r.Max));
    }

    [Fact]
    public void Points_NoBallots()
    {
        var rs = PointsResults.Compute(TestData.Points(10), []);
        Assert.Equal(0, rs.VoterCount);
        Assert.All(rs.Rows, r => { Assert.Equal(0, r.Total); Assert.Equal(0.0, r.Mean); Assert.Equal(0, r.SupporterCount); });
        Assert.Equal(["A", "B", "C"], rs.Rows.Select(r => r.Label));
    }

    [Fact]
    public void Points_TieBreakByLabel()
    {
        var poll = TestData.Points(10, null, false, "Zed", "Alpha", "Mid");
        var rs = PointsResults.Compute(poll, [P(("zed", 5), ("alpha", 5), ("mid", 0))]);
        Assert.Equal(["Alpha", "Zed", "Mid"], rs.Rows.Select(r => r.Label));
    }

    [Fact]
    public void Points_Histogram()
    {
        // option A values across the 4 ballots: 6, 0, 6, 2 ; option C: 0, 5, 0, 7
        var a = PointsResults.Histogram(PBallots, "a");
        Assert.Equal([0, 2, 6], a.Keys);
        Assert.Equal([1, 1, 2], a.Values);
        var c = PointsResults.Histogram(PBallots, "c");
        Assert.Equal(new Dictionary<int, int> { [0] = 2, [5] = 1, [7] = 1 }, c.ToDictionary(k => k.Key, k => k.Value));
    }

    /// <summary>
    /// The example in the editor's Scoring help (DESIGN.md §23.4, PollEditor.razor): four options; two voters rank A, B, C, D
    /// and one ranks B, C, D, A. Borda: B 10, A 9, C 7, D 4. Dowdall: A 2.25, B 2, C 1.17, D 0.83.
    /// </summary>
    [Fact]
    public void Editor_help_scoring_example()
    {
        var options = new[] { "A", "B", "C", "D" }.Select(l => new PollOption(l.ToLowerInvariant(), l, "")).ToList();
        RankedBallot[] ballots = [R(["a", "b", "c", "d"]), R(["a", "b", "c", "d"]), R(["b", "c", "d", "a"])];

        var borda = RankedResults.Compute(new PollQuestion("q1", PollType.Ranked, options) { Scoring = ScoringScheme.Borda }, ballots);
        Assert.Equal(["B", "A", "C", "D"], borda.Rows.Select(r => r.Label));
        Assert.Equal([10.0, 9.0, 7.0, 4.0], borda.Rows.Select(r => r.Score));

        var dowdall = RankedResults.Compute(new PollQuestion("q1", PollType.Ranked, options) { Scoring = ScoringScheme.Dowdall }, ballots);
        Assert.Equal(["A", "B", "C", "D"], dowdall.Rows.Select(r => r.Label));
        Assert.Equal([2.25, 2.0, 1.17, 0.83], dowdall.Rows.Select(r => Math.Round(r.Score, 2)));
    }
}
