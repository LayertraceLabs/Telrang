using Telrang.Core.Ballots;

namespace Telrang.Core.Polls;

/// <summary>An example poll for the home page showcase (DESIGN.md §23.2) and its made-up ballots.</summary>
public sealed record ShowcaseExample<TBallot>(PollDefinition Definition, IReadOnlyList<TBallot> Ballots)
{
    public PollQuestion Question => Definition.Questions[0];
}

/// <summary>
/// The three example polls of the home page (DESIGN.md §23.2), one per type, each with 7 made-up ballots. Built in code
/// through <see cref="PollDraftValidator"/> (so they follow the same rules as real polls) and never stored.
/// </summary>
public static class ShowcasePolls
{
    public const int BallotCount = 7;

    public static ShowcaseExample<RankedBallot> Ranked()
    {
        var draft = Draft("Where do we eat on Friday?", new QuestionDraft
        {
            Type = PollType.Ranked,
            Scoring = ScoringScheme.Borda,
            Bans = 1,
            RankAll = false,
            Options = Labels("Pizzeria Napoli", "Sushi bar", "Thai kitchen", "Burger grill", "Curry house"),
        });
        var def = Build(draft, null);
        var keys = def.Questions[0].Options.Select(o => o.Key).ToArray();
        // Option indexes in ranked order, then the banned one (-1: none).
        (int[] Ranked, int Ban)[] raw =
        [
            ([1, 2, 0], 3),
            ([0, 4, 2], 1),
            ([2, 1, 4, 0], -1),
            ([1, 0], 3),
            ([4, 2, 1], 3),
            ([2, 0, 1, 4, 3], -1),
            ([0, 2], 1),
        ];
        var ballots = raw.Select(b => new RankedBallot(
            b.Ranked.Select(i => keys[i]).ToList(),
            b.Ban < 0 ? [] : [keys[b.Ban]])).ToList();
        return new(def, ballots);
    }

    public static ShowcaseExample<PointsBallot> Points()
    {
        var draft = Draft("Split 100 points over next year's club trips", new QuestionDraft
        {
            Type = PollType.Points,
            Budget = 100,
            MaxPerOption = 60,
            Options = Labels("Hiking weekend in the Ardennes", "City trip to Berlin", "Sailing on the Frisian lakes", "Ski week in Austria"),
        });
        var def = Build(draft, null);
        var keys = def.Questions[0].Options.Select(o => o.Key).ToArray();
        int[][] raw =
        [
            [40, 20, 40, 0],
            [0, 60, 20, 20],
            [30, 10, 30, 30],
            [50, 0, 50, 0],
            [10, 40, 0, 50],
            [25, 25, 25, 25],
            [60, 0, 20, 20],
        ];
        var ballots = raw.Select(b => new PointsBallot(
            keys.Select((k, i) => (k, b[i])).ToDictionary(x => x.k, x => x.Item2, StringComparer.Ordinal))).ToList();
        return new(def, ballots);
    }

    /// <summary>
    /// Five evenings in the month after <paramref name="today"/>'s month (one Saturday with an afternoon and an evening
    /// slot), so the dates are always ahead and fit one calendar month.
    /// </summary>
    public static ShowcaseExample<DatesBallot> Dates(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        var friday = first.AddDays(((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7);
        var evening = new TimeOnly(19, 30);
        var draft = Draft("When can you come to the board games night?", new QuestionDraft
        {
            Type = PollType.Dates,
            Maybe = true,
            Options =
            [
                new() { Date = friday, Start = evening },
                new() { Date = friday.AddDays(1), Start = new TimeOnly(14, 0), End = new TimeOnly(18, 0) },
                new() { Date = friday.AddDays(1), Start = evening, End = new TimeOnly(23, 0) },
                new() { Date = friday.AddDays(7), Start = evening },
                new() { Date = friday.AddDays(8), Start = evening },
                new() { Date = friday.AddDays(13), Start = evening },
            ],
        });
        var def = Build(draft, today);
        var keys = def.Questions[0].Options.Select(o => o.Key).ToArray();
        // One letter per option in option order: y = yes, m = maybe, n = no.
        string[] raw = ["yynmym", "nyymnn", "ymyyny", "nnyyyn", "ymnyym", "yyyynn", "mnyyny"];
        var ballots = raw.Select(b => new DatesBallot(keys.Select((k, i) => (k, b[i] switch
        {
            'y' => Availability.Yes,
            'm' => Availability.Maybe,
            _ => Availability.No,
        })).ToDictionary(x => x.k, x => x.Item2, StringComparer.Ordinal))).ToList();
        return new(def, ballots);
    }

    private static List<OptionDraft> Labels(params string[] labels) => labels.Select(l => new OptionDraft { Label = l }).ToList();

    /// <summary>Every example's description: shows Markdown at work and says the example is inert.</summary>
    public const string Description = "The description supports **Markdown**. This example is *interactive*; nothing is sent or saved.";

    private static PollDraft Draft(string title, QuestionDraft question) => new()
    {
        Title = title,
        Description = Description,
        Names = NamesMode.None,
        ResultsVisibility = ResultsVisibility.Open,
        Questions = [question],
    };

    private static PollDefinition Build(PollDraft draft, DateOnly? today)
    {
        var result = PollDraftValidator.Validate(draft, PollLimits.Default, today: today);
        return result.Definition
            ?? throw new InvalidOperationException("Showcase poll is invalid: " + string.Join(" ", result.Errors.Select(e => e.Message)));
    }
}
