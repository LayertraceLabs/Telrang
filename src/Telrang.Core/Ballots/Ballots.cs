using Telrang.Core.Polls;

namespace Telrang.Core.Ballots;

public sealed record RankedBallot(IReadOnlyList<string> Ranked, IReadOnlyList<string> Banned);

public sealed record PointsBallot(IReadOnlyDictionary<string, int> Points);

public enum Availability { No, Maybe, Yes }

/// <summary>Answers per option key; keys not present read as <see cref="Availability.No"/>.</summary>
public sealed record DatesBallot(IReadOnlyDictionary<string, Availability> Answers);

/// <summary>
/// A choice answer (DESIGN.md §34.3): the picked option keys and whether "Other" was picked. The Other text is not part of it
/// (free text is stored apart, see <see cref="ResponseText"/>). An empty ballot is no answer.
/// </summary>
public sealed record ChoiceBallot(IReadOnlyList<string> Keys, bool Other = false);

/// <summary>
/// A number answer (DESIGN.md §34.3). Stored as a decimal string (<c>{"value": "12.5"}</c>), parsed invariant and never rounded.
/// </summary>
public sealed record NumberBallot(decimal Value)
{
    /// <summary>The value as stored: invariant, no trailing zeros (<c>12.5</c>, not <c>12.50</c>).</summary>
    public string ToInvariantString() => (Value / 1.000000000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads what a voter typed or a payload holds: optional sign, digits, optional <c>.</c> and digits; nothing else (no
    /// thousands separators, exponent or culture). Checks the question's decimals, bounds and the absolute limit; a value with
    /// more decimals than allowed is refused, never rounded. Empty text is no answer (<paramref name="ballot"/> null, no error).
    /// </summary>
    public static bool TryParse(string? text, PollQuestion question, out NumberBallot? ballot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(question);
        ballot = null;
        error = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > 64 || !decimal.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            error = "Enter a number such as 12.5.";
            return false;
        }
        var parsed = new NumberBallot(value);
        error = BallotValidator.ValidateNumber(question, parsed).FirstOrDefault();
        if (error is not null) return false;
        ballot = parsed;
        return true;
    }
}
