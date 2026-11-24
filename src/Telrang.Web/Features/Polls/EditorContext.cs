using System.Text.RegularExpressions;
using Telrang.Core.Polls;
using Microsoft.AspNetCore.Components;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// What the poll form shares with its <see cref="QuestionEditor"/> (DESIGN.md §34.5): the validation result and which errors
/// show, the field ids, the limits, the stored definition on edit, and the change callback (re-validates, keeps the browser
/// draft, re-renders the whole form). Owned by <see cref="PollEditor"/>, one per form.
/// </summary>
public sealed partial class EditorContext
{
    public required string Uid { get; init; }
    public required PollLimits Limits { get; init; }

    /// <summary>On edit: the stored definition (each stored question's type and rules are fixed, DESIGN.md §26).</summary>
    public PollDefinition? Existing { get; set; }

    /// <summary>The validation result of the form as it is now.</summary>
    public DraftResult Result { get; set; } = new(null, []);

    /// <summary>Every error shows (after a save attempt, and on edit), not only those of touched fields.</summary>
    public bool RevealAll { get; set; }

    public HashSet<string> Touched { get; } = new(StringComparer.Ordinal);

    /// <summary>After every edit, with the field path that changed.</summary>
    public EventCallback<string> Changed { get; init; }

    /// <summary>Today in the poll's zone (the dates calendar).</summary>
    public required Func<DateOnly> Today { get; init; }

    /// <summary>The options text of the question shown does not parse: the form cannot be saved.</summary>
    public bool TextInvalid { get; set; }

    /// <summary>The stored question a draft question edits (by key), or null for a new one (and on create).</summary>
    public PollQuestion? StoredOf(QuestionDraft q) =>
        Existing is null || q.Key is null ? null : Existing.Questions.FirstOrDefault(s => string.Equals(s.Key, q.Key, StringComparison.Ordinal));

    public bool Visible(DraftError e) =>
        RevealAll || Touched.Any(t => e.Field == t || e.Field.StartsWith(t + ".", StringComparison.Ordinal)
                                      || e.Field.StartsWith(t + "[", StringComparison.Ordinal));

    /// <summary>The visible messages of one field.</summary>
    public List<string> Errors(string field) => Result.Errors.Where(e => e.Field == field && Visible(e)).Select(e => e.Message).ToList();

    public bool HasError(string field) => Result.Errors.Any(e => e.Field == field && Visible(e));

    /// <summary>Visible errors of a field and everything under it (<c>questions[2]</c>: the question's own and its options').</summary>
    public IEnumerable<DraftError> ErrorsUnder(string prefix) =>
        Result.Errors.Where(e => Visible(e) && (e.Field == prefix || e.Field.StartsWith(prefix + ".", StringComparison.Ordinal)
                                                || e.Field.StartsWith(prefix + "[", StringComparison.Ordinal)));

    public static string Fid(string field) => "f-" + NonId().Replace(field, "-").Trim('-');
    public string Id(string field) => Uid + "-" + Fid(field);
    public string ErrId(string field) => Id(field) + "-err";

    public string? DescribedBy(string field, string? extra = null)
    {
        var ids = new List<string>();
        if (extra is not null) ids.Add(extra);
        if (HasError(field)) ids.Add(ErrId(field));
        return ids.Count == 0 ? null : string.Join(' ', ids);
    }

    public string Invalid(string field) => HasError(field) ? "is-invalid" : "";
    public string? AriaInvalid(string field) => HasError(field) ? "true" : null;

    /// <summary>A radio group's name, unique per form.</summary>
    public string Name(string group) => Uid + "-" + group;

    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex NonId();
}
