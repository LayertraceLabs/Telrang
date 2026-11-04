using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Scoring;

namespace Telrang.Core.Tests;

/// <summary>The home page examples (DESIGN.md §23.2): valid definitions and valid made-up ballots.</summary>
public class ShowcasePollsTests
{
    [Fact]
    public void Ranked_example_matches_the_spec_and_its_ballots_are_valid()
    {
        var x = ShowcasePolls.Ranked();
        var q = x.Question;
        Assert.Equal("Where do we eat on Friday?", x.Definition.Title);
        Assert.Equal((PollType.Ranked, ScoringScheme.Borda, 1, false, 5), (q.Type, q.Scoring, q.MaxBans, q.RankAll, q.Options.Count));
        Assert.Equal(ShowcasePolls.BallotCount, x.Ballots.Count);
        Assert.All(x.Ballots, b => Assert.Empty(BallotValidator.Validate(q, b)));

        // Borda, 5 options (1st = 5 … 5th = 1), by hand: Thai 4+3+5+4+5+4 = 25, Pizzeria 3+5+2+4+4+5 = 23,
        // Sushi 5+4+5+3+3 = 20, Curry 4+3+5+2 = 14, Burger 1.
        var rs = RankedResults.Compute(q, x.Ballots);
        Assert.Equal(["Thai kitchen", "Pizzeria Napoli", "Sushi bar", "Curry house", "Burger grill"], rs.Rows.Select(r => r.Label));
        Assert.Equal([25.0, 23.0, 20.0, 14.0, 1.0], rs.Rows.Select(r => r.Score));
    }

    [Fact]
    public void Points_example_matches_the_spec_and_its_ballots_are_valid()
    {
        var x = ShowcasePolls.Points();
        var q = x.Question;
        Assert.Equal((PollType.Points, 100, 60, 4), (q.Type, q.Budget, q.MaxPerOption, q.Options.Count));
        Assert.Equal(ShowcasePolls.BallotCount, x.Ballots.Count);
        Assert.All(x.Ballots, b => Assert.Empty(BallotValidator.Validate(q, b)));
        Assert.Equal([215L, 185L, 155L, 145L], PointsResults.Compute(q, x.Ballots).Rows.Select(r => r.Total));
    }

    [Theory]
    [InlineData("2026-11-01")]
    [InlineData("2026-12-31")]
    [InlineData("2027-02-28")]
    public void Dates_example_lies_ahead_in_one_month_and_its_ballots_are_valid(string todayText)
    {
        var today = DateOnly.Parse(todayText, System.Globalization.CultureInfo.InvariantCulture);
        var x = ShowcasePolls.Dates(today);
        var q = x.Question;
        Assert.Equal(PollType.Dates, q.Type);
        Assert.True(q.Maybe);
        Assert.Equal(6, q.Options.Count);
        var dates = q.Options.Select(o => o.Date!.Value).ToList();
        Assert.Equal(5, dates.Distinct().Count()); // one date with two slots
        Assert.All(dates, d => Assert.True(d > today));
        Assert.Single(dates.Select(d => (d.Year, d.Month)).Distinct());
        Assert.Equal(DayOfWeek.Friday, dates[0].DayOfWeek);
        Assert.Equal(ShowcasePolls.BallotCount, x.Ballots.Count);
        Assert.All(x.Ballots, b => Assert.Empty(BallotValidator.Validate(q, b)));

        var best = DatesResults.Compute(q, x.Ballots).Rows.Single(r => r.Place == 1);
        Assert.Equal((5, 2), (best.Yes, best.Maybe));
    }
}
