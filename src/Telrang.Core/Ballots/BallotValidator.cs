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

        if (poll.Required && !IsAnswered(poll, ballot)) errors.Add("Rank at least one option.");

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
        if (poll.Required && !IsAnswered(poll, ballot)) errors.Add("Give at least one option points.");
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
        if (poll.Required && !IsAnswered(poll, ballot)) errors.Add("Answer yes or maybe for at least one date.");
        return errors;
    }

    // ---- "Required" (DESIGN.md §34.3): what counts as an answer ----

    /// <summary>Ranked: at least one option ranked (bans alone are no answer).</summary>
    public static bool IsAnswered(PollQuestion poll, RankedBallot ballot) => ballot.Ranked.Count > 0;

    /// <summary>Points: more than 0 points given.</summary>
    public static bool IsAnswered(PollQuestion poll, PointsBallot ballot) => ballot.Points.Values.Any(v => v > 0);

    /// <summary>Dates: at least one yes or maybe.</summary>
    public static bool IsAnswered(PollQuestion poll, DatesBallot ballot) => ballot.Answers.Values.Any(v => v is Availability.Yes or Availability.Maybe);

    /// <summary>Choice: at least one option or Other.</summary>
    public static bool IsAnswered(PollQuestion poll, ChoiceBallot ballot) => ballot.Keys.Count > 0 || ballot.Other;

    /// <summary>Text: anything but white space.</summary>
    public static bool IsAnswered(PollQuestion poll, string? text) => !string.IsNullOrWhiteSpace(text);

    // ---- Choice, Text, Number ----

    /// <summary>
    /// Validates a choice answer. <paramref name="otherText"/> is the Other text (kept apart from the ballot): needed when
    /// <see cref="ChoiceBallot.Other"/> is set, refused when not. An empty ballot is no answer: valid unless required.
    /// Picked options plus Other are the selections, between <see cref="PollQuestion.MinSelect"/> and <see cref="PollQuestion.MaxSelect"/>.
    /// </summary>
    public static IReadOnlyList<string> Validate(PollQuestion poll, ChoiceBallot ballot, string? otherText = null, PollLimits? limits = null)
    {
        var errors = new List<string>();
        if (poll.Type != PollType.Choice)
        {
            errors.Add("This question is not a choice question.");
            return errors;
        }
        limits ??= PollLimits.Default;
        var keys = poll.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var k in ballot.Keys.Distinct().Where(k => !keys.Contains(k))) errors.Add($"Unknown option '{k}'.");
        foreach (var g in ballot.Keys.GroupBy(k => k).Where(g => g.Count() > 1)) errors.Add($"Option '{g.Key}' is picked more than once.");

        var hasText = !string.IsNullOrWhiteSpace(otherText);
        if (ballot.Other && !poll.AllowOther) errors.Add("This question has no Other option.");
        else if (ballot.Other && !hasText) errors.Add("Enter your Other answer.");
        else if (ballot.Other && otherText!.Trim().Length > limits.TextAnswerMax)
            errors.Add($"Shorten the Other answer to {limits.TextAnswerMax} characters (now {otherText.Trim().Length}).");
        else if (!ballot.Other && hasText) errors.Add("Remove the Other text, or pick Other.");

        var selected = ballot.Keys.Distinct().Count() + (ballot.Other ? 1 : 0);
        if (selected == 0)
        {
            if (poll.Required) errors.Add(poll.MinSelect > 1 ? $"Pick at least {poll.MinSelect}." : "Pick an option.");
        }
        else if (selected < poll.MinSelect) errors.Add($"Pick at least {poll.MinSelect}.");
        else if (selected > poll.MaxSelect) errors.Add(poll.MaxSelect == 1 ? "Pick one option." : $"Pick at most {poll.MaxSelect}.");
        return errors;
    }

    /// <summary>
    /// Validates a text answer; null or white space is no answer (valid unless required). Lengths count UTF-16 characters of
    /// the trimmed text, like the other limits; a single-line question takes no line breaks.
    /// </summary>
    public static IReadOnlyList<string> ValidateText(PollQuestion poll, string? text, PollLimits? limits = null)
    {
        var errors = new List<string>();
        if (poll.Type != PollType.Text)
        {
            errors.Add("This question is not a text question.");
            return errors;
        }
        limits ??= PollLimits.Default;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (poll.Required) errors.Add("Enter an answer.");
            return errors;
        }
        var trimmed = text.Trim();
        var max = poll.MaxLength ?? limits.TextAnswerMax;
        if (trimmed.Length > max) errors.Add($"Shorten the answer to {max} characters (now {trimmed.Length}).");
        if (poll.MinLength is int min && trimmed.Length < min) errors.Add($"Write at least {min} characters (now {trimmed.Length}).");
        if (!poll.Multiline && trimmed.AsSpan().ContainsAny('\r', '\n')) errors.Add("Use a single line.");
        return errors;
    }

    /// <summary>
    /// Validates a number answer; null is no answer (valid unless required). More decimals than the question allows is
    /// refused, never rounded.
    /// </summary>
    public static IReadOnlyList<string> ValidateNumber(PollQuestion poll, NumberBallot? ballot)
    {
        var errors = new List<string>();
        if (poll.Type != PollType.Number)
        {
            errors.Add("This question is not a number question.");
            return errors;
        }
        if (ballot is null)
        {
            if (poll.Required) errors.Add("Enter a number.");
            return errors;
        }
        var v = ballot.Value;
        if (Math.Abs(v) > PollRules.NumberAbsMax) errors.Add(PollRules.NumberTooBig());
        else if (PollRules.Scale(v) > poll.Decimals) errors.Add(PollRules.TooManyDecimals(poll.Decimals));
        else
        {
            var unit = poll.Unit.Length == 0 ? "" : " " + poll.Unit;
            if (poll.Min is decimal min && v < min) errors.Add($"Enter {Show(min)}{unit} or more.");
            if (poll.Max is decimal max && v > max) errors.Add($"Enter {Show(max)}{unit} or less.");
        }
        return errors;
    }

    private static string Show(decimal d) => new NumberBallot(d).ToInvariantString();
}
