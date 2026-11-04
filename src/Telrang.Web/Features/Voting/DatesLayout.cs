using System.Globalization;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Web.Features.Voting;

/// <summary>The three layouts of a dates poll (DESIGN.md §18.2); voting and results share them.</summary>
public enum DatesView { Calendar, List, Timeline }

/// <summary>What a cell fragment of <see cref="DatesViews"/> renders: one option in one view.</summary>
public sealed record DatesSlot(PollOption Option, DatesView View);

/// <summary>Labels and groupings for dates polls. Dates are the poll's wall-clock dates; no time zone conversion.</summary>
public static class DatesFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] WeekdayShort = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>"Thu 2026-11-05 19:00": the parser's label with the weekday in front.</summary>
    public static string LongLabel(PollOption o) =>
        o.Date is { } d ? WeekdayShort[MondayIndex(d)] + " " + o.Label : o.Label;

    /// <summary>The slot's own label inside a day: the time ("19:00", "18:30–21:00") or, for a whole day, the day number.</summary>
    public static string SlotLabel(PollOption o) => o.Start switch
    {
        { } s when o.End is { } e => s.ToString("HH:mm", Inv) + "–" + e.ToString("HH:mm", Inv),
        { } s => s.ToString("HH:mm", Inv),
        _ => o.Date?.Day.ToString(Inv) ?? o.Label,
    };

    /// <summary>Weeks of a month, Monday first; null = a day of the neighbouring month (empty cell).</summary>
    public static IEnumerable<DateOnly?[]> Weeks(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var days = DateTime.DaysInMonth(year, month);
        var lead = MondayIndex(first);
        var cells = Enumerable.Repeat<DateOnly?>(null, lead)
            .Concat(Enumerable.Range(0, days).Select(i => (DateOnly?)first.AddDays(i))).ToList();
        while (cells.Count % 7 != 0) cells.Add(null);
        for (var i = 0; i < cells.Count; i += 7) yield return cells.GetRange(i, 7).ToArray();
    }

    /// <summary>"Thursday 5 November 2026", for screen-reader labels.</summary>
    public static string FullDate(DateOnly d) => d.ToString("dddd d MMMM yyyy", Inv);

    /// <summary>"Thu 2026-11-05".</summary>
    public static string DayLabel(DateOnly d) => WeekdayShort[MondayIndex(d)] + " " + d.ToString("yyyy-MM-dd", Inv);

    /// <summary>0 = Monday … 6 = Sunday.</summary>
    public static int MondayIndex(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    public static string MonthName(int year, int month) => new DateOnly(year, month, 1).ToString("MMMM yyyy", Inv);

    public static string MonthShort(DateOnly d) => d.ToString("MMM yyyy", Inv);

    public static string Word(Availability a) => a switch
    {
        Availability.Yes => "yes",
        Availability.Maybe => "maybe",
        _ => "no",
    };

    public static string IconName(Availability a) => a switch
    {
        Availability.Yes => "check",
        Availability.Maybe => "question",
        _ => "x",
    };

    /// <summary>no → yes → maybe → no; no ↔ yes when maybe is off.</summary>
    public static Availability Next(Availability a, bool allowMaybe) => a switch
    {
        Availability.No => Availability.Yes,
        Availability.Yes when allowMaybe => Availability.Maybe,
        _ => Availability.No,
    };
}
