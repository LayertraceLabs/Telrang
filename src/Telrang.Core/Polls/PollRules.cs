using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

namespace Telrang.Core.Polls;

/// <summary>A date, with an optional wall-clock start and end (dates polls).</summary>
public readonly record struct DateSlot(DateOnly Date, TimeOnly? Start, TimeOnly? End);

/// <summary>Rule texts and helpers shared by <see cref="PollDraftValidator"/> and <see cref="OptionListParser"/>.</summary>
public static class PollRules
{
    /// <summary>Longest accepted time zone id (IANA ids are far shorter).</summary>
    public const int TimeZoneMaxLength = 64;

    /// <summary>Most options of a dates question (DESIGN.md §27), whatever the admin's options limit: a day with three slots counts 3.</summary>
    public const int MaxDateOptions = 128;

    /// <summary>The options limit of a question of <paramref name="type"/>: the admin's limit, for dates at most <see cref="MaxDateOptions"/>.</summary>
    public static int OptionsLimit(PollType type, PollLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return type == PollType.Dates ? Math.Min(limits.OptionsMax, MaxDateOptions) : limits.OptionsMax;
    }

    /// <summary>Error for a dates line that is not a date, a time or a range.</summary>
    public const string DateItemHelp = "Write a date like 2026-11-03, a time like 2026-11-03 19:00, or a range like 2026-11-03 to 2026-11-05.";

    public static string TitleTooLong(PollLimits l, int n) => $"Shorten the title to {l.TitleMax} characters (now {n}).";
    public static string DescriptionTooLong(PollLimits l, int n) => $"Shorten the poll description to {l.DescriptionMax} characters (now {n}).";
    public static string LabelTooLong(PollLimits l, int n) => $"Shorten this option label to {l.OptionLabelMax} characters (now {n}).";
    public static string OptionDescriptionTooLong(PollLimits l, string label, int n) =>
        $"Shorten the description of '{Clip(label)}' to {l.OptionDescriptionMax} characters (now {n}).";
    public static string TooManyOptions(int max, int n) => $"Remove options: at most {max} are allowed (now {n}).";
    public static string TooFewOptions(int n) => $"Add at least 2 options (now {n}).";
    public static string BansTooMany(int bans, int options) => $"Allow fewer bans than there are options ({options}).";
    public static string KeepMoreOptionsThanBans(int bans) => $"Keep at least {bans + 1} options: voters may ban {bans}.";
    public static string MaxPerTooBig(int max, int budget) => $"Lower the cap to the budget ({budget}) or less.";
    public static string InvalidTimeZone(string tz) => $"Pick a time zone from the list, such as Europe/Berlin.";
    public static bool TimeZoneOk(string tz) => tz.Length <= TimeZoneMaxLength && !tz.Any(char.IsWhiteSpace);
    public const string EndTimeBeforeStart = "The end time must be after the start time.";
    public const string EndDateBeforeStart = "The end date is before the start date.";

    /// <summary>Lowercase ASCII slug: non-alphanumerics become '-', runs collapsed, ends trimmed. Empty becomes "option".</summary>
    public static string Slugify(string label)
    {
        var sb = new StringBuilder();
        foreach (var c in label)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is >= 'A' and <= 'Z') sb.Append((char)(c + 32));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "option" : s;
    }

    public static string Clip(string label) => label.Length <= 40 ? label : label[..40] + "…";

    /// <summary>
    /// Whether a label holds a control, format, private-use or surrogate character (Unicode Cc, Cf, Co, Cs): bidi overrides
    /// and isolates, zero-width characters, controls. They can make a label read differently from what it is, or reorder
    /// the text around it (DESIGN.md §31, pentest R3-VOTE-1). Checked per UTF-16 unit, so a lone surrogate counts and a
    /// valid pair (an emoji, category So) does not.
    /// </summary>
    public static bool HasHiddenCharacters(string label)
    {
        for (var i = 0; i < label.Length; i++)
        {
            if (char.IsSurrogatePair(label, i))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(label, i) is UnicodeCategory.Format or UnicodeCategory.PrivateUse) return true;
                i++;
                continue;
            }
            if (CharUnicodeInfo.GetUnicodeCategory(label[i]) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate) return true;
        }
        return false;
    }

    public const string HiddenCharacters = "Remove control and invisible formatting characters from the label.";

    /// <summary>
    /// The key of the <paramref name="n"/>-th option added by an edit (DESIGN.md §31): <c>_1</c>, <c>_2</c>, … A slug
    /// (<see cref="Slugify"/>) never holds <c>_</c>, so these never meet a key made at creation.
    /// </summary>
    public static string EditKey(int n) => "_" + n.ToString(CultureInfo.InvariantCulture);

    public static string UniqueKey(string slug, HashSet<string> used)
    {
        var key = slug;
        for (var n = 2; !used.Add(key); n++) key = $"{slug}-{n}";
        return key;
    }

    public static string Dedent(List<string> lines)
    {
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ', '\t').Length).DefaultIfEmpty(0).Min();
        var sb = lines.Select(l => l.Trim().Length == 0 ? "" : l[indent..].TrimEnd());
        return string.Join("\n", sb).Trim('\n', ' ');
    }

    /// <summary>Canonical label: <c>2026-11-07</c>, <c>2026-11-07 18:30</c>, <c>2026-11-07 18:30–21:00</c> (en dash).</summary>
    public static string DateLabel(DateOnly d, TimeOnly? start, TimeOnly? end)
    {
        var label = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (start is TimeOnly s) label += " " + s.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (end is TimeOnly e) label += "–" + e.ToString("HH:mm", CultureInfo.InvariantCulture);
        return label;
    }

    private static readonly Regex DateItem = new(
        @"^(\d{4}-\d{2}-\d{2})(?: to (\d{4}-\d{2}-\d{2}))?(?: (\d{2}:\d{2})(?:[-–](\d{2}:\d{2}))?)?$",
        RegexOptions.CultureInvariant);

    private static bool TryDate(string s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    private static bool TryTime(string s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    /// <summary>Parses one dates item (§18.1) into one slot per day, or the error message.</summary>
    public static bool TryParseDateItem(string text, int optionsMax, out List<DateSlot> slots, out string? error)
    {
        slots = [];
        if (!TryParseDateRange(text, out var from, out var to, out var start, out var end, out error)) return false;
        var days = to.DayNumber - from.DayNumber + 1;
        if (days > optionsMax) { error = TooManyOptions(optionsMax, days); return false; }
        // By index: from.AddDays(k) never passes `to`, so a range ending on 9999-12-31 cannot overflow.
        for (var k = 0; k < days; k++) slots.Add(new DateSlot(from.AddDays(k), start, end));
        return true;
    }

    /// <summary>Parses one dates item (§18.1) into its first and last day and times without expanding it.</summary>
    public static bool TryParseDateRange(string text, out DateOnly from, out DateOnly to, out TimeOnly? start, out TimeOnly? end, out string? error)
    {
        error = null;
        from = to = default;
        start = end = null;
        var m = DateItem.Match(text);
        TimeOnly s0 = default, e0 = default;
        if (!m.Success || !TryDate(m.Groups[1].Value, out from)
            || (m.Groups[2].Success ? !TryDate(m.Groups[2].Value, out to) : (to = from) != from)
            || (m.Groups[3].Success && !TryTime(m.Groups[3].Value, out s0))
            || (m.Groups[4].Success && !TryTime(m.Groups[4].Value, out e0)))
        {
            error = DateItemHelp;
            return false;
        }
        start = m.Groups[3].Success ? s0 : null;
        end = m.Groups[4].Success ? e0 : null;
        if (to < from) { error = EndDateBeforeStart; return false; }
        if (end is TimeOnly e && start is TimeOnly st && e <= st) { error = EndTimeBeforeStart; return false; }
        return true;
    }
}
