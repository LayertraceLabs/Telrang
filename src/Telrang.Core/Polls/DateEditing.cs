namespace Telrang.Core.Polls;

/// <summary>
/// The dates calendar's edits on a question's options (DESIGN.md §27): selecting or deselecting a set of days (a drag,
/// a weekday header, Shift+arrows), copying one day's time slots to every selected day, and the paging of the per-day
/// settings. A refused edit changes nothing and says how many options still fit.
/// </summary>
public static class DateEditing
{
    /// <summary>Days per page of the per-day settings.</summary>
    public const int PageSize = 10;

    /// <summary>The days from <paramref name="a"/> to <paramref name="b"/> inclusive, in date order whichever comes first.</summary>
    public static IReadOnlyList<DateOnly> Range(DateOnly a, DateOnly b)
    {
        var (from, to) = a <= b ? (a, b) : (b, a);
        var days = new List<DateOnly>(to.DayNumber - from.DayNumber + 1);
        // By index: never steps past `to`, so a range ending on 9999-12-31 cannot overflow.
        for (var k = 0; k <= to.DayNumber - from.DayNumber; k++) days.Add(from.AddDays(k));
        return days;
    }

    /// <summary>The days of a month with one weekday (0 = Monday … 6 = Sunday).</summary>
    public static IReadOnlyList<DateOnly> Weekdays(int year, int month, int mondayIndex)
    {
        var first = new DateOnly(year, month, 1);
        var count = DateTime.DaysInMonth(year, month);
        var days = new List<DateOnly>();
        for (var k = 0; k < count; k++)
        {
            var d = first.AddDays(k);
            if (((int)d.DayOfWeek + 6) % 7 == mondayIndex) days.Add(d);
        }
        return days;
    }

    public static bool IsSelected(IEnumerable<OptionDraft> options, DateOnly day) => options.Any(o => o.Date == day);

    /// <summary>A weekday header selects its days unless every one of them is selected; then it deselects them.</summary>
    public static bool HeaderSelects(IEnumerable<OptionDraft> options, IReadOnlyList<DateOnly> days)
    {
        var selected = options.Where(o => o.Date is not null).Select(o => o.Date!.Value).ToHashSet();
        return !days.All(selected.Contains);
    }

    /// <summary>
    /// Gives every day in <paramref name="days"/> the state <paramref name="select"/>: a newly selected day is all day (one
    /// option), a deselected day loses all its options; days already in that state keep their slots and details. Returns
    /// null when applied, else the refusal (the options that still fit) and nothing changes.
    /// </summary>
    public static string? SetDays(List<OptionDraft> options, IEnumerable<DateOnly> days, bool select, int max)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selected = options.Where(o => o.Date is not null).Select(o => o.Date!.Value).ToHashSet();
        if (!select)
        {
            var remove = days.ToHashSet();
            options.RemoveAll(o => o.Date is { } d && remove.Contains(d));
            return null;
        }
        var add = days.Distinct().Where(d => !selected.Contains(d)).ToList();
        if (add.Count == 0) return null;
        if (options.Count + add.Count > max) return SelectRefused(max - options.Count, max);
        options.AddRange(add.Select(d => new OptionDraft { Date = d }));
        Sort(options);
        return null;
    }

    /// <summary>
    /// "Apply to all selected days": every other selected day gets the time slots of <paramref name="day"/> (its own
    /// details stay). Returns null when applied, else the refusal and nothing changes.
    /// </summary>
    public static string? ApplySlots(List<OptionDraft> options, DateOnly day, int max)
    {
        ArgumentNullException.ThrowIfNull(options);
        var slots = options.Where(o => o.Date == day).Select(o => (o.Start, o.End)).ToList();
        var days = options.Where(o => o.Date is not null).Select(o => o.Date!.Value).Distinct().ToList();
        if (slots.Count == 0) return null;
        if (days.Count * slots.Count > max) return ApplyRefused(days.Count, slots.Count, max);
        foreach (var other in days.Where(d => d != day))
        {
            var description = options.Where(o => o.Date == other).Select(o => o.Description).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
            options.RemoveAll(o => o.Date == other);
            options.AddRange(slots.Select(s => new OptionDraft { Date = other, Start = s.Start, End = s.End, Description = description }));
        }
        Sort(options);
        return null;
    }

    /// <summary>Chronological order (date, start, end), as the validator stores dates.</summary>
    public static void Sort(List<OptionDraft> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sorted = options.OrderBy(o => o.Date).ThenBy(o => o.Start).ThenBy(o => o.End).ToList();
        options.Clear();
        options.AddRange(sorted);
    }

    /// <summary>Pages of the per-day settings for <paramref name="days"/> days (at least 1).</summary>
    public static int PageCount(int days) => Math.Max(1, (days + PageSize - 1) / PageSize);

    /// <summary><paramref name="page"/> moved into 0 … <see cref="PageCount"/> − 1.</summary>
    public static int ClampPage(int page, int days) => Math.Clamp(page, 0, PageCount(days) - 1);

    /// <summary>The page that shows <paramref name="day"/> among the selected <paramref name="days"/> (date order), or -1.</summary>
    public static int PageOf(IReadOnlyList<DateOnly> days, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(days);
        var i = days.Count(d => d < day);
        return i < days.Count && days[i] == day ? i / PageSize : -1;
    }

    /// <summary>"12 days · 31 options".</summary>
    public static string CountText(int days, int options) =>
        $"{days} {(days == 1 ? "day" : "days")} · {options} {(options == 1 ? "option" : "options")}";

    /// <summary>A selection that would pass the limit, with the room left.</summary>
    public static string SelectRefused(int fit, int max) => fit <= 0
        ? $"Remove dates or times first: at most {max} options."
        : $"Select at most {fit} more {(fit == 1 ? "day" : "days")}: at most {max} options.";

    /// <summary>"Apply to all" that would pass the limit: how many days fit with this many slots.</summary>
    public static string ApplyRefused(int days, int slots, int max)
    {
        var fit = max / Math.Max(slots, 1);
        return $"Keep at most {fit} {(fit == 1 ? "day" : "days")} for {slots} {(slots == 1 ? "time" : "times")} each: at most {max} options (now {days} days).";
    }

    /// <summary>A time slot added to a day that would pass the limit.</summary>
    public static string SlotRefused(int max) => $"Remove dates or times first: at most {max} options.";
}
