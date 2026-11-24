using System.Text;
using Telrang.Core.Polls;

namespace Telrang.Core.Ballots;

/// <summary>
/// The free text of one response (DESIGN.md §34.3): text answers and "Other" texts, stored apart from the scored payload as one
/// map keyed <c>q5</c> (a text question) or <c>q2.other</c> (a choice question's Other). Per-answer rules are
/// <see cref="BallotValidator.ValidateText"/> and <see cref="BallotValidator.Validate(PollQuestion, ChoiceBallot, string?, PollLimits?)"/>;
/// this checks which keys exist and the response's total size.
/// </summary>
public static class ResponseText
{
    /// <summary>The map key of a text question's answer.</summary>
    public static string AnswerKey(PollQuestion q) => q.Key;

    /// <summary>The map key of a choice question's Other text.</summary>
    public static string OtherKey(PollQuestion q) => q.Key + ".other";

    /// <summary>Total UTF-8 size of the texts (the values; what <see cref="PollLimits.ResponseTextBytesMax"/> counts, before compression).</summary>
    public static int Bytes(IReadOnlyDictionary<string, string> texts) =>
        texts.Values.Sum(v => Encoding.UTF8.GetByteCount(v));

    /// <summary>
    /// Checks the keys against the questions (a text question, a choice question with Other; nothing else may carry text) and the
    /// total size against <see cref="PollLimits.ResponseTextBytesMax"/>. Empty list = valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<PollQuestion> questions, IReadOnlyDictionary<string, string> texts, PollLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(texts);
        limits ??= PollLimits.Default;
        var errors = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in questions)
        {
            if (q.Type == PollType.Text) known.Add(AnswerKey(q));
            else if (q.Type == PollType.Choice && q.AllowOther) known.Add(OtherKey(q));
        }
        foreach (var k in texts.Keys.Where(k => !known.Contains(k)).Order(StringComparer.Ordinal)) errors.Add($"Unknown text answer '{k}'.");
        var bytes = Bytes(texts);
        if (bytes > limits.ResponseTextBytesMax)
            errors.Add($"Shorten your text answers to {limits.ResponseTextBytesMax} bytes in total (now {bytes}).");
        return errors;
    }

    /// <summary>
    /// The most free text any response to <paramref name="questions"/> can hold, in UTF-8 bytes: the sum of the per-question
    /// maxima (3 bytes per UTF-16 character at most), capped by <see cref="PollLimits.ResponseTextBytesMax"/>. For sizing a form
    /// limit.
    /// </summary>
    public static long MaxPossibleBytes(IReadOnlyList<PollQuestion> questions, PollLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(questions);
        limits ??= PollLimits.Default;
        long total = 0;
        foreach (var q in questions)
        {
            if (q.Type == PollType.Text) total += 3L * (q.MaxLength ?? limits.TextAnswerMax);
            else if (q.Type == PollType.Choice && q.AllowOther) total += 3L * limits.TextAnswerMax;
        }
        return Math.Min(total, limits.ResponseTextBytesMax);
    }
}
