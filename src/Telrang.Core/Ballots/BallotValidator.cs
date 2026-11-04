using Telrang.Core.Polls;

namespace Telrang.Core.Ballots;

/// <summary>Authoritative ballot validation, per question. Returns human-readable errors; empty list = valid.</summary>
public static class BallotValidator
{
    public static IReadOnlyList<string> Validate(PollQuestion poll, RankedBallot ballot)
    {
        var errors = new List<string>();
        if (poll.Type != PollType.Ranked)
        {
            errors.Add("This question is not a ranked question.");
            return errors;
        }
        var keys = poll.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var k in ballot.Ranked.Concat(ballot.Banned).Distinct())
            if (!keys.Contains(k)) errors.Add($"Unknown option '{k}'.");

        foreach (var g in ballot.Ranked.GroupBy(k => k).Where(g => g.Count() > 1))
            errors.Add($"Option '{g.Key}' is ranked more than once.");
        foreach (var g in ballot.Banned.GroupBy(k => k).Where(g => g.Count() > 1))
            errors.Add($"Option '{g.Key}' is banned more than once.");

        var banned = ballot.Banned.ToHashSet(StringComparer.Ordinal);
        foreach (var k in ballot.Ranked.Distinct().Where(banned.Contains))
            errors.Add($"Option '{k}' cannot be both ranked and banned.");

        if (ballot.Banned.Distinct().Count() > poll.MaxBans)
            errors.Add($"At most {poll.MaxBans} option(s) can be banned.");

        if (poll.RankAll)
        {
            var ranked = ballot.Ranked.ToHashSet(StringComparer.Ordinal);
            foreach (var o in poll.Options.Where(o => !banned.Contains(o.Key) && !ranked.Contains(o.Key)))
                errors.Add($"Option '{o.Label}' must be ranked or banned.");
        }
        return errors;
    }

    public static IReadOnlyList<string> Validate(PollQuestion poll, PointsBallot ballot)
    {
        var errors = new List<string>();
        if (poll.Type != PollType.Points)
        {
            errors.Add("This question is not a points question.");
            return errors;
        }
        var keys = poll.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        long sum = 0;
        foreach (var (key, value) in ballot.Points)
        {
            if (!keys.Contains(key)) { errors.Add($"Unknown option '{key}'."); continue; }
            if (value < 0) { errors.Add($"Option '{key}' has a negative value."); continue; }
            if (poll.MaxPerOption is int max && value > max)
                errors.Add($"Option '{key}' exceeds the maximum of {max} per option.");
            sum += value;
        }
        if (poll.Budget is int budget)
        {
            if (sum > budget) errors.Add($"Total {sum} exceeds the budget of {budget}.");
            else if (poll.SpendAll && sum != budget) errors.Add($"The full budget of {budget} must be spent (spent {sum}).");
        }
        return errors;
    }

    public static IReadOnlyList<string> Validate(PollQuestion poll, DatesBallot ballot)
    {
        var errors = new List<string>();
        if (poll.Type != PollType.Dates)
        {
            errors.Add("This question is not a dates question.");
            return errors;
        }
        var keys = poll.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in ballot.Answers)
        {
            if (!keys.Contains(key)) { errors.Add($"Unknown option '{key}'."); continue; }
            if (!System.Enum.IsDefined(value)) { errors.Add($"Option '{key}' has an invalid answer."); continue; }
            if (value == Availability.Maybe && !poll.Maybe)
                errors.Add($"Option '{key}' cannot be 'maybe' in this poll.");
        }
        return errors;
    }
}
