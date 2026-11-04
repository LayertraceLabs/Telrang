using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

public class OptionListParserTests
{
    private static List<OptionDraft> Ok(string text, PollType type = PollType.Ranked, PollLimits? limits = null)
    {
        var r = OptionListParser.Parse(text, type, limits ?? PollLimits.Default);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => $"{e.Line}: {e.Message}")));
        return r.Options.ToList();
    }

    private static IReadOnlyList<OptionListError> Fail(string text, PollType type = PollType.Ranked, PollLimits? limits = null)
    {
        var r = OptionListParser.Parse(text, type, limits ?? PollLimits.Default);
        Assert.False(r.Success);
        return r.Errors;
    }

    [Theory]
    [InlineData("- A\n- B")]
    [InlineData("* A\n* B")]
    [InlineData("+ A\n+ B")]
    [InlineData("1. A\n2. B")]
    [InlineData("1) A\n2) B")]
    [InlineData("A\nB")]
    [InlineData("A  \n\n\nB  ")]
    [InlineData("- A\r\n- B\r\n")]
    public void Markers_whitespace_and_blank_lines(string text)
    {
        Assert.Equal(["A", "B"], Ok(text).Select(o => o.Label));
    }

    [Fact]
    public void Only_one_marker_is_stripped_and_non_markers_survive()
    {
        Assert.Equal(["1. first", "-5 degrees", "1.5 hours", "C#"], Ok("- 1. first\n-5 degrees\n1.5 hours\nC#").Select(o => o.Label));
    }

    [Fact]
    public void Indented_lines_are_the_description()
    {
        var o = Ok("- Paris\n  City of **lights**.\n\n  Second paragraph.\n    code\n- Rome\n\tTabbed");
        Assert.Equal("City of **lights**.\n\nSecond paragraph.\n  code", o[0].Description);
        Assert.Equal("Tabbed", o[1].Description);
        Assert.Equal("", Ok("- A\n- B")[0].Description);
    }

    [Fact]
    public void Leading_indented_line_is_an_item()
    {
        Assert.Equal(["A", "B"], Ok("  A\n- B").Select(o => o.Label));
    }

    [Fact]
    public void Empty_label_is_an_error_with_line()
    {
        var e = Fail("- A\n-\n- B");
        Assert.Equal(2, e[0].Line);
    }

    [Fact]
    public void Limits_with_line_numbers()
    {
        var limits = new PollLimits(OptionLabelMax: 3, OptionDescriptionMax: 4, OptionsMax: 2);
        var e = Fail("- ok\n- toolong\n  12345\n- ok2\n- third", limits: limits);
        Assert.Contains(e, x => x.Line == 2 && x.Message.StartsWith("Shorten this option label"));
        Assert.Contains(e, x => x.Line == 2 && x.Message.StartsWith("Shorten the description"));
        Assert.Contains(e, x => x.Line == 4 && x.Message.StartsWith("Remove options"));
    }

    [Fact]
    public void Duplicates_are_listed_twice()
    {
        var e = Fail("- A\n- B\n- a");
        Assert.Equal(3, Assert.Single(e).Line);
        Assert.Contains("listed twice", e[0].Message);
    }

    [Fact]
    public void Dates_forms_ranges_and_sorting()
    {
        var o = Ok("- 2026-11-07 18:30-21:00\n  desc\n- 2026-11-03\n- 2026-11-05 19:00\n- 2026-11-10 to 2026-11-11 08:00", PollType.Dates);
        Assert.Equal(["2026-11-03", "2026-11-05 19:00", "2026-11-07 18:30–21:00", "2026-11-10 08:00", "2026-11-11 08:00"], o.Select(x => x.Label));
        Assert.Equal(new DateOnly(2026, 11, 7), o[2].Date);
        Assert.Equal(new TimeOnly(21, 0), o[2].End);
        Assert.Equal("desc", o[2].Description);
    }

    [Fact]
    public void Dates_errors_use_the_help_text()
    {
        var e = Fail("- 2026-11-03\n- tomorrow\n- 2026-11-05 to 2026-11-04\n- 2026-11-06 19:00-18:00\n- 2026-11-03", PollType.Dates);
        Assert.Equal("Write a date like 2026-11-03, a time like 2026-11-03 19:00, or a range like 2026-11-03 to 2026-11-05.", e[0].Message);
        Assert.Equal(2, e[0].Line);
        Assert.Equal("The end date is before the start date.", e[1].Message);
        Assert.Equal("The end time must be after the start time.", e[2].Message);
        Assert.Equal("'2026-11-03' is listed twice.", e[3].Message);
        Assert.Equal(5, e[3].Line);
    }

    [Fact]
    public void Dates_range_too_long()
    {
        var e = Fail("- 2026-01-01 to 2026-12-31", PollType.Dates, new PollLimits(OptionsMax: 10));
        Assert.Contains("at most 10", e[0].Message);
    }

    [Fact]
    public void Round_trip_text_options()
    {
        var options = new List<OptionDraft>
        {
            new() { Label = "Paris", Description = "City of **lights**.\n\n- a list\n    indented code\nlast" },
            new() { Label = "1. odd label" },
            new() { Label = "- dash" },
            new() { Label = "Rome", Description = "x" },
        };
        var text = OptionListParser.Format(options, PollType.Ranked);
        Assert.Equal(options, Ok(text));
    }

    [Fact]
    public void Round_trip_dates()
    {
        var options = new List<OptionDraft>
        {
            new() { Label = "2026-11-03", Date = new DateOnly(2026, 11, 3), Description = "all day\nsecond" },
            new() { Label = "2026-11-05 19:00", Date = new DateOnly(2026, 11, 5), Start = new TimeOnly(19, 0) },
            new() { Label = "2026-11-07 18:30–21:00", Date = new DateOnly(2026, 11, 7), Start = new TimeOnly(18, 30), End = new TimeOnly(21, 0), Description = "snacks" },
        };
        var text = OptionListParser.Format(options, PollType.Dates);
        Assert.Contains("- 2026-11-07 18:30–21:00\n  snacks", text);
        Assert.Equal(options, Ok(text, PollType.Dates));
    }

    [Fact]
    public void Format_empty_is_empty()
    {
        Assert.Equal("", OptionListParser.Format([], PollType.Ranked));
        Assert.Empty(Ok(""));
    }

    [Fact]
    public void Text_options_feed_the_validator()
    {
        var draft = new PollDraft
        {
            Title = "T",
            Questions = [new QuestionDraft { Type = PollType.Dates, Options = Ok("- 2026-11-03 to 2026-11-05", PollType.Dates) }],
        };
        var r = PollDraftValidator.Validate(draft, PollLimits.Default);
        Assert.True(r.Success);
        Assert.Equal(3, r.Definition!.Questions[0].Options.Count);
    }
}

/// <summary>AV-03: the parser's work is bounded by the poll limits, never by the input.</summary>
public class OptionListParserBoundsTests
{
    private static (OptionListResult Result, long Allocated, TimeSpan Elapsed) Measure(string text, PollType type, PollLimits? limits = null)
    {
        OptionListParser.Parse("- warm up", type, PollLimits.Default);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var r = OptionListParser.Parse(text, type, limits ?? PollLimits.Default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (r, allocated, sw.Elapsed);
    }

    private static string Repeat(string line, int maxChars)
    {
        var sb = new System.Text.StringBuilder(maxChars);
        while (sb.Length + line.Length <= maxChars) sb.Append(line);
        return sb.ToString();
    }

    [Fact]
    public void Max_text_length_fits_every_valid_list()
    {
        var l = PollLimits.Default;
        Assert.Equal(l.OptionsMax * (l.OptionLabelMax + 2 * l.OptionDescriptionMax + OptionListParser.OptionOverhead), OptionListParser.MaxTextLength(l));
        // The longest options Format writes: full labels, descriptions of one-character lines.
        var desc = string.Join("\n", Enumerable.Repeat("x", (l.OptionDescriptionMax + 1) / 2));
        var options = Enumerable.Range(0, l.OptionsMax)
            .Select(i => new OptionDraft { Label = i.ToString("D3") + new string('L', l.OptionLabelMax - 3), Description = desc }).ToList();
        var text = OptionListParser.Format(options, PollType.Ranked);
        Assert.True(text.Length <= OptionListParser.MaxTextLength(l), $"{text.Length}");
        var r = OptionListParser.Parse(text, PollType.Ranked, l);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        Assert.Equal(options, r.Options);
        Assert.Equal(int.MaxValue, OptionListParser.MaxTextLength(new PollLimits(OptionLabelMax: int.MaxValue, OptionDescriptionMax: int.MaxValue, OptionsMax: int.MaxValue)));
    }

    [Fact]
    public void A_megabyte_of_ranges_is_refused_before_parsing()
    {
        var text = Repeat("2026-01-01 to 2026-04-10\n", 1 << 20);
        var (r, allocated, elapsed) = Measure(text, PollType.Dates);
        var e = Assert.Single(r.Errors);
        Assert.Equal(0, e.Line);
        Assert.Equal($"Shorten the list to {OptionListParser.MaxTextLength(PollLimits.Default)} characters (now {text.Length}).", e.Message);
        Assert.Empty(r.Options);
        Assert.True(allocated < 64 * 1024, $"{allocated} bytes");
        Assert.True(elapsed < TimeSpan.FromSeconds(1), elapsed.ToString());
    }

    [Fact]
    public void Half_a_million_lines_are_refused_before_parsing()
    {
        var text = Repeat("a\n", 2 * 524_288);
        var (r, allocated, _) = Measure(text, PollType.Ranked);
        Assert.StartsWith("Shorten the list to", Assert.Single(r.Errors).Message);
        Assert.True(allocated < 64 * 1024, $"{allocated} bytes");
    }

    [Fact]
    public void Ranges_at_the_length_limit_stay_bounded()
    {
        var text = Repeat("2026-01-01 to 2026-04-10\n", OptionListParser.MaxTextLength(PollLimits.Default));
        var (r, allocated, elapsed) = Measure(text, PollType.Dates);
        Assert.Equal(OptionListParser.MaxErrors, r.Errors.Count);
        Assert.True(r.MoreErrors > 0);
        Assert.All(r.Errors, e => Assert.Contains("listed twice", e.Message));
        Assert.True(r.Options.Count <= PollLimits.Default.OptionsMax);
        Assert.True(allocated < 16 * 1024 * 1024, $"{allocated} bytes");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), elapsed.ToString());
    }

    [Fact]
    public void Lines_at_the_length_limit_stay_bounded()
    {
        var text = Repeat("a\n", OptionListParser.MaxTextLength(PollLimits.Default));
        var (r, allocated, elapsed) = Measure(text, PollType.Ranked);
        Assert.Equal(OptionListParser.MaxErrors + 1, r.Errors.Count);
        Assert.Equal($"Remove options: at most 100 are allowed (now {text.Length / 2}).", r.Errors[^1].Message);
        Assert.True(r.Options.Count <= PollLimits.Default.OptionsMax);
        Assert.True(allocated < 32 * 1024 * 1024, $"{allocated} bytes");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), elapsed.ToString());
    }

    [Fact]
    public void Too_many_options_is_one_error_with_the_full_count()
    {
        var text = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"- option {i}\n  details {i}"));
        var r = OptionListParser.Parse(text, PollType.Ranked, PollLimits.Default);
        var e = Assert.Single(r.Errors);
        Assert.Equal("Remove options: at most 100 are allowed (now 500).", e.Message);
        Assert.Equal(201, e.Line);
        Assert.Equal(100, r.Options.Count);
    }

    [Fact]
    public void Too_many_dates_counts_ranges_arithmetically()
    {
        var text = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"- {new DateOnly(2026, 1, 1).AddDays(i * 10):yyyy-MM-dd} to {new DateOnly(2026, 1, 1).AddDays(i * 10 + 9):yyyy-MM-dd}"));
        var r = OptionListParser.Parse(text, PollType.Dates, PollLimits.Default);
        Assert.Equal("Remove options: at most 100 are allowed (now 500).", Assert.Single(r.Errors).Message);
        Assert.Equal(100, r.Options.Count);
    }

    [Fact]
    public void Errors_are_capped_with_a_count()
    {
        var text = string.Join("\n", Enumerable.Range(0, 60).Select(_ => "- tomorrow"));
        var r = OptionListParser.Parse(text, PollType.Dates, PollLimits.Default);
        Assert.Equal(OptionListParser.MaxErrors, r.Errors.Count);
        Assert.Equal(40, r.MoreErrors);
        Assert.False(r.Success);
    }

    [Fact]
    public void A_range_ending_on_the_last_calendar_day_does_not_overflow()
    {
        Assert.True(PollRules.TryParseDateItem("9999-12-30 to 9999-12-31", 10, out var slots, out _));
        Assert.Equal([new DateOnly(9999, 12, 30), DateOnly.MaxValue], slots.Select(x => x.Date));
        var r = OptionListParser.Parse("- 9999-12-29 to 9999-12-31", PollType.Dates, PollLimits.Default);
        Assert.True(r.Success);
        Assert.Equal(3, r.Options.Count);
    }

    [Fact]
    public void Dates_text_reads_years_0001_to_9999_and_the_edge_times()
    {
        var r = OptionListParser.Parse("- 9999-12-31 23:59\n- 0001-01-01 00:00-00:30\n- 0001-01-01", PollType.Dates, PollLimits.Default);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        Assert.Equal(["0001-01-01", "0001-01-01 00:00–00:30", "9999-12-31 23:59"], r.Options.Select(o => o.Label));
        Assert.Equal("- 0001-01-01\n- 0001-01-01 00:00–00:30\n- 9999-12-31 23:59", OptionListParser.Format(r.Options, PollType.Dates));
        Assert.False(OptionListParser.Parse("- 0000-12-31\n- 2026-01-01", PollType.Dates, PollLimits.Default).Success);
        Assert.False(OptionListParser.Parse("- 10000-01-01\n- 2026-01-01", PollType.Dates, PollLimits.Default).Success);
    }

    [Fact]
    public void Dates_text_counts_against_the_128_cap()
    {
        var high = new PollLimits(OptionsMax: 1000);
        Assert.True(OptionListParser.Parse("- 2027-01-01 to 2027-05-08", PollType.Dates, high).Success); // 128 days
        var r = OptionListParser.Parse("- 2027-01-01 to 2027-05-08\n- 2027-06-01 19:00", PollType.Dates, high);
        Assert.Equal("Remove options: at most 128 are allowed (now 129).", Assert.Single(r.Errors).Message);
        Assert.Contains(OptionListParser.Parse("- 2027-01-01 to 2027-12-31", PollType.Dates, high).Errors, e => e.Message.Contains("at most 128", StringComparison.Ordinal));
        // Text options keep the admin's limit.
        Assert.True(OptionListParser.Parse(string.Join("\n", Enumerable.Range(0, 300).Select(i => $"- O{i}")), PollType.Ranked, high).Success);
    }
}
