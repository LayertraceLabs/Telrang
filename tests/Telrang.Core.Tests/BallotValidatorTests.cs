using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

public class BallotValidatorTests
{
    private static RankedBallot R(string[] ranked, params string[] banned) => new(ranked, banned);
    private static PointsBallot P(params (string, int)[] p) => new(p.ToDictionary(x => x.Item1, x => x.Item2));

    [Fact]
    public void Ranked_Valid()
        => Assert.Empty(BallotValidator.Validate(TestData.Ranked(maxBans: 1), R(["a", "b"], "c")));

    [Fact]
    public void Ranked_EmptyBallotAllowedWithoutRankAll()
        => Assert.Empty(BallotValidator.Validate(TestData.Ranked(), R([])));

    [Fact]
    public void Ranked_UnknownKey()
        => Assert.Single(BallotValidator.Validate(TestData.Ranked(), R(["a", "zzz"])));

    [Fact]
    public void Ranked_UnknownBannedKey()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Ranked(maxBans: 1), R(["a"], "zzz")));

    [Fact]
    public void Ranked_DuplicateRanked()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Ranked(), R(["a", "a"])));

    [Fact]
    public void Ranked_DuplicateBanned()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Ranked(maxBans: 2), R([], "c", "c")));

    [Fact]
    public void Ranked_RankedAndBanned()
        => Assert.Contains(BallotValidator.Validate(TestData.Ranked(maxBans: 2), R(["a"], "a")), e => e.Contains("both"));

    [Fact]
    public void Ranked_TooManyBans()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Ranked(maxBans: 1), R([], "b", "c")));

    [Fact]
    public void Ranked_BansDisabled()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Ranked(maxBans: 0), R(["a"], "c")));

    [Fact]
    public void Ranked_BansAtLimitOk()
        => Assert.Empty(BallotValidator.Validate(TestData.Ranked(maxBans: 2), R(["a"], "b", "c")));

    [Fact]
    public void Ranked_RankAllMissing()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Ranked(rankAll: true), R(["a", "b"])));

    [Fact]
    public void Ranked_RankAllSatisfied()
        => Assert.Empty(BallotValidator.Validate(TestData.Ranked(rankAll: true), R(["b", "a", "c"])));

    [Fact]
    public void Ranked_RankAllBannedExempt()
        => Assert.Empty(BallotValidator.Validate(TestData.Ranked(maxBans: 1, rankAll: true), R(["b", "a"], "c")));

    [Fact]
    public void Ranked_WrongPollType()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Points(), R([])));

    [Fact]
    public void Points_Valid()
        => Assert.Empty(BallotValidator.Validate(TestData.Points(10), P(("a", 4), ("b", 6))));

    [Fact]
    public void Points_UnderBudgetOkWithoutSpendAll()
        => Assert.Empty(BallotValidator.Validate(TestData.Points(10), P(("a", 1))));

    [Fact]
    public void Points_EmptyOkWithoutSpendAll()
        => Assert.Empty(BallotValidator.Validate(TestData.Points(10), P()));

    [Fact]
    public void Points_UnknownKey()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Points(10), P(("zzz", 1))));

    [Fact]
    public void Points_Negative()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Points(10), P(("a", -1), ("b", 5))));

    [Fact]
    public void Points_OverMaxPerOption()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Points(10, maxPer: 5), P(("a", 6))));

    [Fact]
    public void Points_AtMaxPerOptionOk()
        => Assert.Empty(BallotValidator.Validate(TestData.Points(10, maxPer: 5), P(("a", 5), ("b", 5))));

    [Fact]
    public void Points_OverBudget()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Points(10), P(("a", 6), ("b", 5))));

    [Fact]
    public void Points_SpendAllUnder()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Points(10, spendAll: true), P(("a", 9))));

    [Fact]
    public void Points_SpendAllExact()
        => Assert.Empty(BallotValidator.Validate(TestData.Points(10, spendAll: true), P(("a", 9), ("c", 1))));

    [Fact]
    public void Points_NoOverflowOnHugeValues()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Points(10), P(("a", int.MaxValue), ("b", int.MaxValue))));

    [Fact]
    public void Points_WrongPollType()
        => Assert.NotEmpty(BallotValidator.Validate(TestData.Ranked(), P()));
}
