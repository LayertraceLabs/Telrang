using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

/// <summary>The dates calendar's edits (DESIGN.md §27): ranges, weekday headings, "Apply to all", the 128 cap, paging.</summary>
public class DateEditingTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static List<OptionDraft> Days(params string[] days) => days.Select(d => new OptionDraft { Date = D(d) }).ToList();

    private static string[] Labels(IEnumerable<OptionDraft> options) =>
        options.Select(o => PollRules.DateLabel(o.Date!.Value, o.Start, o.End)).ToArray();

    [Fact]
    public void Range_runs_in_date_order_either_way_and_reaches_the_ends_of_the_calendar()
    {
        Assert.Equal([D("2026-11-28"), D("2026-11-29"), D("2026-11-30"), D("2026-12-01")], DateEditing.Range(D("2026-12-01"), D("2026-11-28")));
        Assert.Equal([D("9999-12-30"), DateOnly.MaxValue], DateEditing.Range(DateOnly.MaxValue, D("9999-12-30")));
        Assert.Equal([DateOnly.MinValue, D("0001-01-02")], DateEditing.Range(DateOnly.MinValue, D("0001-01-02")));
        Assert.Single(DateEditing.Range(D("2026-11-03"), D("2026-11-03")));
    }

    [Fact]
    public void Selecting_adds_all_day_options_and_keeps_existing_days_as_they_are()
    {
        var options = Days("2026-11-04");
        options.Add(new OptionDraft { Date = D("2026-11-05"), Start = new TimeOnly(19, 0), Key = "k5", Description = "Hall" });
        Assert.Null(DateEditing.SetDays(options, DateEditing.Range(D("2026-11-03"), D("2026-11-06")), true, 100));
        Assert.Equal(["2026-11-03", "2026-11-04", "2026-11-05 19:00", "2026-11-06"], Labels(options));
        Assert.Equal("k5", options[2].Key); // a day that was selected keeps its slots, key and details
        Assert.Equal("Hall", options[2].Description);

        Assert.Null(DateEditing.SetDays(options, DateEditing.Range(D("2026-11-04"), D("2026-11-05")), false, 100));
        Assert.Equal(["2026-11-03", "2026-11-06"], Labels(options));
    }

    [Fact]
    public void A_selection_past_the_limit_is_refused_whole_saying_how_many_fit()
    {
        var options = Days("2026-11-01", "2026-11-02", "2026-11-03");
        var refused = DateEditing.SetDays(options, DateEditing.Range(D("2026-11-10"), D("2026-11-14")), true, 6);
        Assert.Equal("Select at most 3 more days: at most 6 options.", refused);
        Assert.Equal(3, options.Count);
        Assert.Equal("Select at most 1 more day: at most 4 options.", DateEditing.SetDays(options, DateEditing.Range(D("2026-11-10"), D("2026-11-11")), true, 4));
        Assert.Equal("Remove dates or times first: at most 3 options.", DateEditing.SetDays(options, [D("2026-11-10")], true, 3));
        // Already selected days cost nothing; deselecting is never refused.
        Assert.Null(DateEditing.SetDays(options, DateEditing.Range(D("2026-11-01"), D("2026-11-03")), true, 3));
        Assert.Null(DateEditing.SetDays(options, DateEditing.Range(D("2026-11-01"), D("2026-11-30")), false, 3));
        Assert.Empty(options);
    }

    [Fact]
    public void Weekday_headings_select_unless_every_one_is_selected()
    {
        var tuesdays = DateEditing.Weekdays(2026, 12, 1);
        Assert.Equal([D("2026-12-01"), D("2026-12-08"), D("2026-12-15"), D("2026-12-22"), D("2026-12-29")], tuesdays);
        Assert.Equal(4, DateEditing.Weekdays(2026, 12, 6).Count); // Sundays
        Assert.Equal(5, DateEditing.Weekdays(9999, 12, 4).Count); // 9999-12-31 is a Friday
        Assert.Equal(DayOfWeek.Monday, DateEditing.Weekdays(1, 1, 0)[0].DayOfWeek);

        var options = Days("2026-12-08");
        Assert.True(DateEditing.HeaderSelects(options, tuesdays));
        Assert.Null(DateEditing.SetDays(options, tuesdays, DateEditing.HeaderSelects(options, tuesdays), 100));
        Assert.Equal(5, options.Count);
        Assert.False(DateEditing.HeaderSelects(options, tuesdays));
        Assert.Null(DateEditing.SetDays(options, tuesdays, DateEditing.HeaderSelects(options, tuesdays), 100));
        Assert.Empty(options);
    }

    [Fact]
    public void Apply_to_all_copies_the_slots_and_is_refused_past_the_limit()
    {
        var options = Days("2026-11-03", "2026-11-04");
        options[1].Description = "Garden";
        options.Insert(0, new OptionDraft { Date = D("2026-11-02"), Start = new TimeOnly(18, 0), End = new TimeOnly(19, 0) });
        options.Insert(0, new OptionDraft { Date = D("2026-11-02"), Start = new TimeOnly(20, 0) });
        Assert.Null(DateEditing.ApplySlots(options, D("2026-11-02"), 6));
        Assert.Equal(["2026-11-02 18:00–19:00", "2026-11-02 20:00", "2026-11-03 18:00–19:00", "2026-11-03 20:00", "2026-11-04 18:00–19:00", "2026-11-04 20:00"],
            Labels(options));
        Assert.Equal("Garden", options[4].Description);

        options.Add(new OptionDraft { Date = D("2026-11-05") });
        Assert.Equal("Keep at most 3 days for 2 times each: at most 7 options (now 4 days).", DateEditing.ApplySlots(options, D("2026-11-02"), 7));
        Assert.Equal(7, options.Count);
    }

    [Fact]
    public void Day_settings_are_paged_by_ten()
    {
        Assert.Equal(1, DateEditing.PageCount(0));
        Assert.Equal(1, DateEditing.PageCount(10));
        Assert.Equal(2, DateEditing.PageCount(11));
        Assert.Equal(13, DateEditing.PageCount(128));
        Assert.Equal(1, DateEditing.ClampPage(5, 12));
        Assert.Equal(0, DateEditing.ClampPage(-1, 12));
        var days = Enumerable.Range(0, 25).Select(i => D("2026-11-01").AddDays(i)).ToList();
        Assert.Equal(0, DateEditing.PageOf(days, D("2026-11-10")));
        Assert.Equal(1, DateEditing.PageOf(days, D("2026-11-11")));
        Assert.Equal(2, DateEditing.PageOf(days, D("2026-11-25")));
        Assert.Equal(-1, DateEditing.PageOf(days, D("2026-12-25")));
        Assert.Equal("12 days · 31 options", DateEditing.CountText(12, 31));
        Assert.Equal("1 day · 1 option", DateEditing.CountText(1, 1));
    }
}
