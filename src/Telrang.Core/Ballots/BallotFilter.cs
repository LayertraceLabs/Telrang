using Telrang.Core.Polls;

namespace Telrang.Core.Ballots;

/// <summary>
/// Ballots stay raw (DESIGN.md §26): a stored ballot may name options the question no longer has. Every reader goes
/// through these, which drop unknown keys: ranked, the options below a removed one move up; points and dates, its answer
/// no longer counts. A new option is simply missing (unranked, 0 points, no).
/// <para>A reader of many ballots builds the question's key set once (<see cref="For"/>) and filters each ballot with it;
/// the static <c>Known</c> overloads build it per call (PERFORMANCE.md, "Known bottlenecks").</para>
/// </summary>
public static class BallotFilter
{
    /// <summary>The current option keys of <paramref name="q"/>, to filter many ballots with.</summary>
    public static KnownKeys For(PollQuestion q) => new(q);

    public static RankedBallot Known(PollQuestion q, RankedBallot ballot) => For(q).Filter(ballot);

    public static PointsBallot Known(PollQuestion q, PointsBallot ballot) => For(q).Filter(ballot);

    public static DatesBallot Known(PollQuestion q, DatesBallot ballot) => For(q).Filter(ballot);
}

/// <summary>A question's current option keys (<see cref="BallotFilter.For"/>); the filters drop every other key.</summary>
public sealed class KnownKeys
{
    private readonly HashSet<string> keys;

    internal KnownKeys(PollQuestion q)
    {
        ArgumentNullException.ThrowIfNull(q);
        keys = q.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
    }

    public bool Contains(string key) => keys.Contains(key);

    public RankedBallot Filter(RankedBallot ballot) =>
        new(ballot.Ranked.Where(keys.Contains).Distinct().ToList(), ballot.Banned.Where(keys.Contains).Distinct().ToList());

    public PointsBallot Filter(PointsBallot ballot) =>
        new(ballot.Points.Where(p => keys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    public DatesBallot Filter(DatesBallot ballot) =>
        new(ballot.Answers.Where(a => keys.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));
}
