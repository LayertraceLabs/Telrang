using System.Globalization;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Web.Features.Results;

/// <summary>
/// One answer as one cell of text (DESIGN.md §34.5): the ballots view of a structured poll and the ballots CSV share it, so
/// both read the same. Ranked: "Soup &gt; Salad; banned: Fish"; points: "Paris: 7; Rome: 3"; dates: "Mon 3 Nov: yes;
/// Wed 5 Nov: maybe" (no left out); choice: the picked labels, then "Other" (the owner: "Other: text"); number: the value
/// (in the view with its unit); text: the owner the text, everyone else <see cref="Answered"/>. Not answered: empty.
/// Free text only ever for the owner (§34.3).
/// </summary>
public static class AnswerText
{
    /// <summary>What a voter sees for a text answer: that there is one, never the text.</summary>
    public const string Answered = "answered";

    /// <summary>The owner's cell for free text whose cipher cannot be read (a lost key ring, DESIGN.md §34.3).</summary>
    public const string Unreadable = "unreadable";

    public const string OtherLabel = "Other";

    public enum Form { View, Csv }

    /// <param name="owner">The viewer is the poll's owner: free text is shown.</param>
    /// <param name="free">The response's free text, decrypted (owner only; null = none read or unreadable).</param>
    public static string Cell(PollQuestion q, CellLayout layout, int index, ResponseCells r, bool owner,
        IReadOnlyDictionary<string, string>? free, Form form)
    {
        var cells = layout.Slice(r, index, q);
        switch (q.Type)
        {
            case PollType.Ranked:
            {
                var ranked = new List<(int Position, string Label)>();
                var banned = new List<string>();
                for (var i = 0; i < cells.Length; i++)
                {
                    if (cells[i] > 0) ranked.Add((cells[i], q.Options[i].Label));
                    else if (cells[i] < 0) banned.Add(q.Options[i].Label);
                }
                var text = string.Join(" > ", ranked.OrderBy(x => x.Position).Select(x => x.Label));
                if (banned.Count > 0) text += (text.Length > 0 ? "; " : "") + "banned: " + string.Join(", ", banned);
                return text;
            }
            case PollType.Points:
            {
                var parts = new List<string>();
                for (var i = 0; i < cells.Length; i++)
                    if (cells[i] > 0) parts.Add(q.Options[i].Label + ": " + cells[i].ToString(CultureInfo.InvariantCulture));
                return string.Join("; ", parts);
            }
            case PollType.Dates:
            {
                var parts = new List<string>();
                for (var i = 0; i < cells.Length; i++)
                {
                    var a = (Availability)cells[i];
                    if (a is Availability.Yes or Availability.Maybe) parts.Add(q.Options[i].Label + ": " + (a == Availability.Yes ? "yes" : "maybe"));
                }
                return string.Join("; ", parts);
            }
            case PollType.Choice:
            {
                var parts = new List<string>();
                for (var i = 0; i < q.Options.Count; i++)
                    if (cells[i] != 0) parts.Add(q.Options[i].Label);
                if (cells[q.Options.Count] != 0)
                {
                    var other = owner ? Free(free, ResponseText.OtherKey(q)) : null;
                    parts.Add(string.IsNullOrWhiteSpace(other) ? OtherLabel : OtherLabel + ": " + other);
                }
                return string.Join("; ", parts);
            }
            case PollType.Number:
            {
                if (cells[0] == 0) return "";
                var value = new NumberBallot(r.Numbers[layout.NumberIndex[index]]).ToInvariantString();
                return form == Form.View && q.Unit.Length > 0 ? value + " " + q.Unit : value;
            }
            case PollType.Text:
                if (cells[0] == 0) return "";
                return owner ? Free(free, ResponseText.AnswerKey(q)) ?? Unreadable : Answered;
            default:
                throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
        }
    }

    private static string? Free(IReadOnlyDictionary<string, string>? free, string key) =>
        free is null ? null : free.GetValueOrDefault(key) ?? "";
}
