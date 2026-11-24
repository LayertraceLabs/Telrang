using System.Text;

namespace Telrang.Core.Polls;

/// <summary>What happened to one option in an edit (DESIGN.md §26, the change log).</summary>
/// <remarks>
/// The change log (<c>PollChange.Kind</c>) also records <see cref="TitleChanged"/> and <see cref="DescriptionChanged"/>
/// (the poll's text, not an option) and <see cref="Earlier"/>, the one entry older entries beyond the cap are merged into
/// (DESIGN.md §31).
/// </remarks>
public enum OptionChangeKind
{
    Added, Removed, Renamed, DetailsChanged, TitleChanged, DescriptionChanged, Earlier,
    /// <summary>DESIGN.md §34.5: a question was added (key and label: the new question's, label = its prompt).</summary>
    QuestionAdded,
    /// <summary>A question was removed (key: the retired key, to count the ballots that answered it; label = its last prompt).</summary>
    QuestionRemoved,
    /// <summary>A question's prompt changed (label = the new prompt, old label = the old one).</summary>
    QuestionRenamed,
}

/// <param name="Key">The option's key (for a removal: the retired key, to count the ballots that answered it).</param>
/// <param name="Label">The option's label after the change (removed: its last label).</param>
/// <param name="OldLabel">Renamed: the label before.</param>
public sealed record OptionChange(OptionChangeKind Kind, string Key, string Label, string? OldLabel = null)
{
    /// <summary>
    /// The question the entry is about (DESIGN.md §34.5), set by <see cref="OptionChanges.Between(PollDefinition, PollDefinition)"/> on
    /// every question and option entry, so a structured poll can name it; null on the poll's own entries (title, description)
    /// and on the entries of <see cref="OptionChanges.Between(PollQuestion, PollQuestion)"/>, which sees one question only.
    /// </summary>
    public string? QuestionKey { get; init; }

    /// <summary>The question's prompt after the change (removed question: its last one); empty for a question without prompt.</summary>
    public string? QuestionLabel { get; init; }
}

/// <summary>The option changes between two versions of a question, by option key (DESIGN.md §26).</summary>
public static class OptionChanges
{
    /// <summary>
    /// The changes an edit records (DESIGN.md §26, §31): the title (<see cref="OptionChangeKind.TitleChanged"/>, label the
    /// new title, old label the old one), the description (<see cref="OptionChangeKind.DescriptionChanged"/>, no text),
    /// then the questions (DESIGN.md §34.5): removed ones (old order), then per question of <paramref name="after"/> in its order:
    /// added, or renamed (prompt, same exemption) followed by its option changes (<see cref="Between(PollQuestion, PollQuestion)"/>,
    /// each naming the question). Pages, groups, help, order and moves between pages are not logged. Title and description
    /// changes have the same case and whitespace exemption as labels; option keys of the poll-text entries are empty.
    /// </summary>
    public static IReadOnlyList<OptionChange> Between(PollDefinition before, PollDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<OptionChange>();
        if (!SameText(before.Title, after.Title)) changes.Add(new OptionChange(OptionChangeKind.TitleChanged, "", after.Title, before.Title));
        if (!SameText(before.DescriptionMarkdown, after.DescriptionMarkdown)) changes.Add(new OptionChange(OptionChangeKind.DescriptionChanged, "", ""));
        var old = before.Questions.ToDictionary(q => q.Key, StringComparer.Ordinal);
        var now = after.Questions.Select(q => q.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var q in before.Questions.Where(q => !now.Contains(q.Key)))
            changes.Add(new OptionChange(OptionChangeKind.QuestionRemoved, q.Key, q.Prompt) { QuestionKey = q.Key, QuestionLabel = q.Prompt });
        foreach (var q in after.Questions)
        {
            if (!old.TryGetValue(q.Key, out var was))
            {
                changes.Add(new OptionChange(OptionChangeKind.QuestionAdded, q.Key, q.Prompt) { QuestionKey = q.Key, QuestionLabel = q.Prompt });
                continue;
            }
            if (!SameText(was.Prompt, q.Prompt))
                changes.Add(new OptionChange(OptionChangeKind.QuestionRenamed, q.Key, q.Prompt, was.Prompt) { QuestionKey = q.Key, QuestionLabel = q.Prompt });
            changes.AddRange(Between(was, q).Select(c => c with { QuestionKey = q.Key, QuestionLabel = q.Prompt }));
        }
        return changes;
    }

    /// <summary>
    /// Removals first (in the old order), then per option of <paramref name="after"/> in its order: added, renamed, details
    /// changed. A rename that differs only in letter case or whitespace (<see cref="SameText"/>) is not a change; a new
    /// label and new details are two changes. Reordering is not a change.
    /// </summary>
    public static IReadOnlyList<OptionChange> Between(PollQuestion before, PollQuestion after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var old = before.Options.ToDictionary(o => o.Key, StringComparer.Ordinal);
        var now = after.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var changes = new List<OptionChange>();
        foreach (var o in before.Options.Where(o => !now.Contains(o.Key)))
            changes.Add(new OptionChange(OptionChangeKind.Removed, o.Key, o.Label));
        foreach (var o in after.Options)
        {
            if (!old.TryGetValue(o.Key, out var was))
            {
                changes.Add(new OptionChange(OptionChangeKind.Added, o.Key, o.Label));
                continue;
            }
            if (!SameText(was.Label, o.Label)) changes.Add(new OptionChange(OptionChangeKind.Renamed, o.Key, o.Label, was.Label));
            if (!string.Equals(was.DescriptionMarkdown, o.DescriptionMarkdown, StringComparison.Ordinal))
                changes.Add(new OptionChange(OptionChangeKind.DetailsChanged, o.Key, o.Label));
        }
        return changes;
    }

    /// <summary>
    /// True when both texts differ at most in letter case and whitespace ("Pizza  Napoli" and "pizza napoli"), compared
    /// after canonical (NFC) normalisation. Punctuation, symbols, numerals, marks and every other character count
    /// (DESIGN.md §31, pentest R3-VOTE-1: "+10%" and "-10%" are different options).
    /// </summary>
    public static bool SameText(string a, string b) => string.Equals(Folded(a), Folded(b), StringComparison.Ordinal);

    private static string Folded(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
