using System.Text;
using System.Text.RegularExpressions;

namespace Telrang.Core.Polls;

public sealed record OptionListError(int Line, string Message);

/// <param name="MoreErrors">Errors found beyond the first <see cref="OptionListParser.MaxErrors"/> (not listed).</param>
public sealed record OptionListResult(IReadOnlyList<OptionDraft> Options, IReadOnlyList<OptionListError> Errors, int MoreErrors = 0)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// The Text mode of the options editor (DESIGN.md §20.1): one option per line, indented lines under an option
/// are its description, dates use the §18.1 line forms. <see cref="Format"/> is the inverse.
/// </summary>
/// <remarks>
/// The work is bounded by the poll limits, never by the input (AV-03): text longer than <see cref="MaxTextLength"/>
/// is refused before parsing, no option past the options limit (<see cref="PollRules.OptionsLimit"/>) is materialised (the rest are only
/// counted, date ranges arithmetically) and at most <see cref="MaxErrors"/> errors are listed.
/// </remarks>
public static class OptionListParser
{
    /// <summary>Most errors listed (plus the "too many options" one); the rest are counted in <see cref="OptionListResult.MoreErrors"/>.</summary>
    public const int MaxErrors = 20;

    /// <summary>Characters allowed per option beyond its label and description (marker, newlines, blank lines).</summary>
    public const int OptionOverhead = 16;

    private static readonly Regex Marker = new(@"^(?:[-*+]|\d{1,9}[.)])(?:\s+|$)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The longest text accepted: every option at its longest, with room for the marker and for indenting each
    /// description line (a description counts at most twice: one indent of two per line of at least one character
    /// and its newline), so any valid list written by <see cref="Format"/> fits.
    /// </summary>
    public static int MaxTextLength(PollLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var n = (double)Math.Max(limits.OptionsMax, 0) * ((double)Math.Max(limits.OptionLabelMax, 0) + 2.0 * Math.Max(limits.OptionDescriptionMax, 0) + OptionOverhead);
        return n >= int.MaxValue ? int.MaxValue : (int)n;
    }

    public static string TextTooLong(int max, int n) => $"Shorten the list to {max} characters (now {n}).";

    private sealed class Item
    {
        public string Label = "";
        public int Line;
        public readonly List<string> Desc = [];
    }

    private sealed class Errors
    {
        public readonly List<OptionListError> List = [];
        public int More;
        public bool Full => List.Count >= MaxErrors;
        public void Add(int line, string message)
        {
            if (List.Count < MaxErrors) List.Add(new OptionListError(line, message));
            else More++;
        }
    }

    public static OptionListResult Parse(string text, PollType type, PollLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        text = (text ?? "").TrimStart('\uFEFF');
        var maxLength = MaxTextLength(limits);
        if (text.Length > maxLength) return new OptionListResult([], [new OptionListError(0, TextTooLong(maxLength, text.Length))]);

        var errors = new Errors();
        var max = PollRules.OptionsLimit(type, limits); // dates: at most PollRules.MaxDateOptions (DESIGN.md §27)
        var options = new List<OptionDraft>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSlots = new HashSet<DateSlot>();
        long count = 0;   // options listed, also past the limit
        var overLine = 0; // the line of the first option past the limit

        // Past the limit an item is only counted: no description is kept, no option made, no duplicate checked.
        void Finish(Item item)
        {
            var desc = PollRules.Dedent(item.Desc);
            if (type == PollType.Dates)
            {
                if (!PollRules.TryParseDateRange(item.Label, out var from, out var to, out var start, out var end, out var error))
                {
                    errors.Add(item.Line, error!);
                    return;
                }
                var days = to.DayNumber - from.DayNumber + 1;
                if (days > max) { errors.Add(item.Line, PollRules.TooManyOptions(max, days)); return; }
                for (var k = 0; k < days; k++)
                {
                    var slot = new DateSlot(from.AddDays(k), start, end);
                    if (!seenSlots.Add(slot))
                    {
                        if (errors.Full) errors.More++; // no message is built that would not be listed
                        else errors.Add(item.Line, $"'{PollRules.DateLabel(slot.Date, slot.Start, slot.End)}' is listed twice.");
                        continue;
                    }
                    count++;
                    if (count > max) { Over(item.Line, days - k - 1); return; }
                    var label = PollRules.DateLabel(slot.Date, slot.Start, slot.End);
                    options.Add(new OptionDraft { Label = label, Description = desc, Date = slot.Date, Start = slot.Start, End = slot.End });
                }
                if (desc.Length > limits.OptionDescriptionMax)
                    errors.Add(item.Line, PollRules.OptionDescriptionTooLong(limits, item.Label, desc.Length));
            }
            else
            {
                if (item.Label.Length > limits.OptionLabelMax)
                    errors.Add(item.Line, PollRules.LabelTooLong(limits, item.Label.Length));
                if (desc.Length > limits.OptionDescriptionMax)
                    errors.Add(item.Line, PollRules.OptionDescriptionTooLong(limits, item.Label, desc.Length));
                if (!seenLabels.Add(item.Label)) errors.Add(item.Line, $"'{item.Label}' is listed twice.");
                count++;
                if (count > max) { Over(item.Line, 0); return; }
                options.Add(new OptionDraft { Label = item.Label, Description = desc });
            }
        }

        void Over(int line, long rest)
        {
            if (overLine == 0) overLine = line;
            count += rest;
        }

        // Counts an item past the limit without keeping it: a date line by its day count, any other line as one.
        void Count(string label)
        {
            if (type == PollType.Dates && PollRules.TryParseDateRange(label, out var from, out var to, out _, out _, out _))
                count += to.DayNumber - from.DayNumber + 1;
            else count++;
        }

        var lines = text.Split('\n');
        Item? current = null;
        var skipping = false; // inside an item past the limit: its description lines are skipped
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var blank = line.Trim().Length == 0;
            if ((current is not null || skipping) && (blank || line[0] == ' ' || line[0] == '\t'))
            {
                current?.Desc.Add(blank ? "" : line);
                continue;
            }
            if (blank) continue;
            if (current is not null) { Finish(current); current = null; }
            var label = Marker.Replace(line.Trim(), "", 1).Trim();
            if (label.Length == 0)
            {
                errors.Add(i + 1, "Option label is empty.");
                skipping = false;
                continue;
            }
            if (count > max) { Count(label); skipping = true; continue; }
            skipping = false;
            current = new Item { Label = label, Line = i + 1 };
        }
        if (current is not null) Finish(current);

        // Always listed, also past the cap: it is the error that explains the rest.
        if (count > max)
            errors.List.Add(new OptionListError(overLine, PollRules.TooManyOptions(max, (int)Math.Min(count, int.MaxValue))));

        var result = type == PollType.Dates
            ? options.OrderBy(o => o.Date).ThenBy(o => o.Start).ThenBy(o => o.End).ToList()
            : options;
        return new OptionListResult(result, errors.List, errors.More);
    }

    /// <summary>Writes the options as text that <see cref="Parse"/> reads back to the same options.</summary>
    public static string Format(IEnumerable<OptionDraft> options, PollType type)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sb = new StringBuilder();
        foreach (var o in options)
        {
            var label = type == PollType.Dates && o.Date is DateOnly d ? PollRules.DateLabel(d, o.Start, o.End) : (o.Label ?? "");
            sb.Append("- ").Append(label).Append('\n');
            var desc = (o.Description ?? "").Replace("\r\n", "\n").Trim('\n', ' ');
            if (desc.Length == 0) continue;
            foreach (var l in desc.Split('\n'))
                sb.Append(l.Trim().Length == 0 ? "" : "  " + l).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
