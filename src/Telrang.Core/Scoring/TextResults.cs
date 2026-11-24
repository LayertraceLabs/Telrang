using Telrang.Core.Polls;

namespace Telrang.Core.Scoring;

/// <param name="AnswerCount">Answers that are not blank. The texts themselves are owner-only (DESIGN.md §34.3) and never leave the stored form through here.</param>
public sealed record TextResultSet(int AnswerCount);

public static class TextResults
{
    /// <summary>The number of answers: texts that are not null, empty or white space.</summary>
    public static TextResultSet Compute(PollQuestion poll, IEnumerable<string?> answers)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(answers);
        return new TextResultSet(answers.Count(a => !string.IsNullOrWhiteSpace(a)));
    }
}
