using System.Text.Json;
using System.Text.Json.Nodes;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Features.Polls;

namespace Telrang.Web.Features.Voting;

/// <summary>
/// One question's answer in a response (DESIGN.md §34.3): the ballot of the question's type, or for a text question the
/// text. <see cref="OtherText"/> is a choice question's Other text. Free text (<see cref="Text"/>, <see cref="OtherText"/>)
/// never goes into the scored payload; it is stored apart (<see cref="ResponseAnswers.Texts"/>, <c>Ballot.TextCipher</c>).
/// </summary>
public sealed record QuestionAnswer
{
    public RankedBallot? Ranked { get; init; }
    public PointsBallot? Points { get; init; }
    public DatesBallot? Dates { get; init; }
    public ChoiceBallot? Choice { get; init; }
    public NumberBallot? Number { get; init; }

    /// <summary>A text question's answer (trimmed).</summary>
    public string? Text { get; init; }

    /// <summary>A choice question's Other text (trimmed).</summary>
    public string? OtherText { get; init; }
}

/// <summary>
/// A whole response, one <see cref="QuestionAnswer"/> per question key: how the vote page, the carry field and
/// <see cref="BallotService"/> move answers around, and how they become the stored parts (the scored
/// <see cref="Payload"/> and the free <see cref="Texts"/>). A question without an entry is not answered.
/// </summary>
public static class ResponseAnswers
{
    /// <summary>
    /// The answers of a stored response, per question of <paramref name="def"/>: scored parts read through the per-type readers
    /// (removed options dropped, DESIGN.md §26), free text from <paramref name="texts"/> (the decrypted <c>TextCipher</c>; null =
    /// none or unreadable). A question without a part (and, for text, without a text) gets no entry.
    /// </summary>
    public static Dictionary<string, QuestionAnswer> Read(PollDefinition def, string payloadJson, IReadOnlyDictionary<string, string>? texts)
    {
        ArgumentNullException.ThrowIfNull(def);
        var answers = new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal);
        // One parse for the whole payload, whatever the number of questions: this runs on every request of the vote page.
        using var doc = JsonDocument.Parse(payloadJson);
        foreach (var q in def.Questions)
        {
            if (Read(q, doc.RootElement, texts) is { } a) answers[q.Key] = a;
        }
        return answers;
    }

    private static QuestionAnswer? Read(PollQuestion q, JsonElement root, IReadOnlyDictionary<string, string>? texts)
    {
        var has = PollJson.TryPart(root, q.Key, out var part);
        switch (q.Type)
        {
            case PollType.Ranked:
                return has ? new QuestionAnswer { Ranked = BallotFilter.For(q).Filter(PollJson.RankedFrom(part)) } : null;
            case PollType.Points:
                return has ? new QuestionAnswer { Points = BallotFilter.For(q).Filter(PollJson.PointsFrom(part)) } : null;
            case PollType.Dates:
                return has ? new QuestionAnswer { Dates = BallotFilter.For(q).Filter(PollJson.DatesFrom(part)) } : null;
            case PollType.Choice:
            {
                if (!has) return null;
                var c = BallotFilter.For(q).Filter(PollJson.ChoiceFrom(part));
                if (c.Other && !q.AllowOther) c = c with { Other = false };
                var other = c.Other ? texts?.GetValueOrDefault(ResponseText.OtherKey(q)) : null;
                return new QuestionAnswer { Choice = c, OtherText = other };
            }
            case PollType.Text:
                return texts?.GetValueOrDefault(ResponseText.AnswerKey(q)) is { } t && !string.IsNullOrWhiteSpace(t)
                    ? new QuestionAnswer { Text = t }
                    : null;
            case PollType.Number:
                return has && PollJson.NumberFrom(part) is { } n ? new QuestionAnswer { Number = n } : null;
            default:
                throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
        }
    }

    /// <summary>The answer in its stored shape: dates "no" left out, free text trimmed, an empty choice or blank text as no answer.</summary>
    public static QuestionAnswer? Normalize(PollQuestion q, QuestionAnswer? a)
    {
        if (a is null) return null;
        return q.Type switch
        {
            PollType.Ranked => a.Ranked is { } r ? new QuestionAnswer { Ranked = new RankedBallot(r.Ranked.ToList(), r.Banned.ToList()) } : null,
            PollType.Points => a.Points is { } p ? new QuestionAnswer { Points = new PointsBallot(new Dictionary<string, int>(p.Points, StringComparer.Ordinal)) } : null,
            // No answers are stored as absent keys: a missing key reads as no, so the payload stays small.
            PollType.Dates => a.Dates is { } d
                ? new QuestionAnswer { Dates = new DatesBallot(d.Answers.Where(x => x.Value != Availability.No).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)) }
                : null,
            PollType.Choice => a.Choice is { } c && (c.Keys.Count > 0 || c.Other || !string.IsNullOrWhiteSpace(a.OtherText))
                ? new QuestionAnswer { Choice = new ChoiceBallot(c.Keys.ToList(), c.Other), OtherText = Blank(a.OtherText) }
                : null,
            PollType.Text => Blank(a.Text) is { } t ? new QuestionAnswer { Text = t } : null,
            PollType.Number => a.Number is { } n ? new QuestionAnswer { Number = n } : null,
            _ => throw new ArgumentOutOfRangeException(nameof(q), q.Type, null),
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// The errors of one answer (DESIGN.md §34.3), the type's validator; <paramref name="requireAnswer"/> false checks
    /// everything but "Required" (the Back button merges what is valid that way, §34.4). A missing answer is an empty ballot.
    /// </summary>
    public static IReadOnlyList<string> Validate(PollQuestion q, QuestionAnswer? a, PollLimits limits, bool requireAnswer = true)
    {
        ArgumentNullException.ThrowIfNull(q);
        if (!requireAnswer && q.Required) q = q with { Required = false };
        return q.Type switch
        {
            PollType.Ranked => BallotValidator.Validate(q, a?.Ranked ?? new RankedBallot([], [])),
            PollType.Points => BallotValidator.Validate(q, a?.Points ?? new PointsBallot(new Dictionary<string, int>())),
            PollType.Dates => BallotValidator.Validate(q, a?.Dates ?? new DatesBallot(new Dictionary<string, Availability>())),
            PollType.Choice => BallotValidator.Validate(q, a?.Choice ?? new ChoiceBallot([]), a?.OtherText, limits),
            PollType.Text => BallotValidator.ValidateText(q, a?.Text, limits),
            PollType.Number => BallotValidator.ValidateNumber(q, a?.Number),
            _ => throw new ArgumentOutOfRangeException(nameof(q), q.Type, null),
        };
    }

    /// <summary>The scored payload: one part per answered question in definition order; free text never.</summary>
    public static string Payload(PollDefinition def, IReadOnlyDictionary<string, QuestionAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(answers);
        var parts = new List<KeyValuePair<string, JsonNode>>();
        var simple = VoteLayout.IsSimple(def); // a simple poll keeps an empty ballot (a voter, as before 0.4)
        foreach (var q in def.Questions)
        {
            if (Normalize(q, answers.GetValueOrDefault(q.Key)) is not { } a) continue;
            JsonNode? node = q.Type switch
            {
                PollType.Ranked => simple || PollJson.HasAnswer(a.Ranked!) ? PollJson.RankedNode(a.Ranked!) : null,
                PollType.Points => simple || PollJson.HasAnswer(a.Points!) ? PollJson.PointsNode(a.Points!) : null,
                PollType.Dates => simple || PollJson.HasAnswer(a.Dates!) ? PollJson.DatesNode(a.Dates!) : null,
                PollType.Choice => a.Choice!.Keys.Count > 0 || a.Choice.Other ? PollJson.ChoiceNode(a.Choice) : null,
                PollType.Text => PollJson.TextNode(),
                PollType.Number => PollJson.NumberNode(a.Number!),
                _ => throw new ArgumentOutOfRangeException(nameof(def), q.Type, null),
            };
            if (node is not null) parts.Add(new(q.Key, node));
        }
        return PollJson.SerializeParts(parts);
    }

    /// <summary>The free text of the response (<see cref="ResponseText"/> keys: <c>q5</c>, <c>q2.other</c>), trimmed; empty when none.</summary>
    public static Dictionary<string, string> Texts(PollDefinition def, IReadOnlyDictionary<string, QuestionAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(answers);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var q in def.Questions)
        {
            if (Normalize(q, answers.GetValueOrDefault(q.Key)) is not { } a) continue;
            if (q.Type == PollType.Text && a.Text is { } t) texts[ResponseText.AnswerKey(q)] = t;
            else if (q.Type == PollType.Choice && a.Choice is { Other: true } && a.OtherText is { } o) texts[ResponseText.OtherKey(q)] = o;
        }
        return texts;
    }

    /// <summary>Only the answers to questions the definition has, normalised; removed options dropped (BallotFilter).</summary>
    public static Dictionary<string, QuestionAnswer> Known(PollDefinition def, IReadOnlyDictionary<string, QuestionAnswer> answers)
    {
        var known = new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal);
        foreach (var q in def.Questions)
        {
            if (Normalize(q, answers.GetValueOrDefault(q.Key)) is not { } a) continue;
            var f = BallotFilter.For(q);
            known[q.Key] = q.Type switch
            {
                PollType.Ranked => a with { Ranked = f.Filter(a.Ranked!) },
                PollType.Points => a with { Points = f.Filter(a.Points!) },
                PollType.Dates => a with { Dates = f.Filter(a.Dates!) },
                PollType.Choice => a with { Choice = f.Filter(a.Choice!) },
                PollType.Text or PollType.Number => a,
                _ => throw new ArgumentOutOfRangeException(nameof(def), q.Type, null),
            };
        }
        return known;
    }
}
