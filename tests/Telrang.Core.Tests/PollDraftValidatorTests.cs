using Telrang.Core.Polls;

namespace Telrang.Core.Tests;

public class PollDraftValidatorTests
{
    private static OptionDraft O(string label, string desc = "") => new() { Label = label, Description = desc };

    private static OptionDraft D(string date, string? start = null, string? end = null, string desc = "") => new()
    {
        Date = DateOnly.Parse(date),
        Start = start is null ? null : TimeOnly.Parse(start),
        End = end is null ? null : TimeOnly.Parse(end),
        Description = desc,
    };

    private static PollDraft Ranked(params string[] labels) => new()
    {
        Title = "T",
        Questions = [new QuestionDraft { Type = PollType.Ranked, Options = labels.Select(l => O(l)).ToList() }],
    };

    private static PollDraft Points(int? budget, params string[] labels) => new()
    {
        Title = "T",
        Questions = [new QuestionDraft { Type = PollType.Points, Budget = budget, Options = labels.Select(l => O(l)).ToList() }],
    };

    private static PollDraft DatesDraft(params OptionDraft[] options) => new()
    {
        Title = "T",
        Questions = [new QuestionDraft { Type = PollType.Dates, Options = options.ToList() }],
    };

    private static PollDefinition Ok(PollDraft d, PollLimits? limits = null, PollDefinition? existing = null)
    {
        var r = PollDraftValidator.Validate(d, limits ?? PollLimits.Default, existing);
        Assert.True(r.Definition is not null, string.Join("; ", r.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return r.Definition!;
    }

    private static IReadOnlyList<DraftError> Fail(PollDraft d, PollLimits? limits = null, PollDefinition? existing = null)
    {
        var r = PollDraftValidator.Validate(d, limits ?? PollLimits.Default, existing);
        Assert.Null(r.Definition);
        Assert.NotEmpty(r.Errors);
        return r.Errors;
    }

    private static void HasError(IReadOnlyList<DraftError> errors, string field, string? contains = null) =>
        Assert.Contains(errors, e => e.Field == field && (contains is null || e.Message.Contains(contains)));

    private static PollQuestion Q(PollDefinition d) => Assert.Single(d.Questions);

    private static void AssertSame(PollDefinition a, PollDefinition b)
    {
        Assert.Equal(a with { Questions = [] }, b with { Questions = [] });
        Assert.Equal(a.Questions.Count, b.Questions.Count);
        for (var i = 0; i < a.Questions.Count; i++)
        {
            Assert.Equal(a.Questions[i] with { Options = [] }, b.Questions[i] with { Options = [] });
            Assert.Equal(a.Questions[i].Options, b.Questions[i].Options);
        }
    }

    // ---- shape ----

    [Fact]
    public void Ranked_all_settings()
    {
        var draft = new PollDraft
        {
            Title = "Team trip",
            Description = "Pick in order.",
            VoterMode = VoterMode.Accounts,
            Responses = ResponsePolicy.Many,
            Names = NamesMode.Required,
            ResultsVisibility = ResultsVisibility.AfterVote,
            ClosesOn = new DateOnly(2026, 11, 30),
            TimeZone = "Europe/Berlin",
            Questions =
            [
                new QuestionDraft
                {
                    Type = PollType.Ranked, Scoring = ScoringScheme.Dowdall, Bans = 2, RankAll = true,
                    Options = [O("Paris", "City of **lights**.\n\nSecond paragraph."), O("Rome"), O("Berlin"), O("Paris!")],
                },
            ],
        };
        var def = Ok(draft);
        Assert.Equal("Team trip", def.Title);
        Assert.Equal("Pick in order.", def.DescriptionMarkdown);
        Assert.Equal(VoterMode.Accounts, def.Voters);
        Assert.Equal(ResponsePolicy.Many, def.Responses);
        Assert.Equal(NamesMode.Required, def.Names);
        Assert.Equal(ResultsVisibility.AfterVote, def.Results);
        Assert.Equal(new DateOnly(2026, 11, 30), def.ClosesOn);
        Assert.Equal("Europe/Berlin", def.TimeZone);
        var q = Q(def);
        Assert.Equal("q1", q.Key);
        Assert.Equal(PollType.Ranked, q.Type);
        Assert.Equal(ScoringScheme.Dowdall, q.Scoring);
        Assert.Equal(2, q.MaxBans);
        Assert.True(q.RankAll);
        Assert.Equal(["paris", "rome", "berlin", "paris-2"], q.Options.Select(o => o.Key));
        Assert.Equal("City of **lights**.\n\nSecond paragraph.", q.Options[0].DescriptionMarkdown);
    }

    [Fact]
    public void Defaults()
    {
        var def = Ok(Ranked("A", "B"));
        Assert.Equal(VoterMode.Public, def.Voters);
        Assert.Equal(ResponsePolicy.One, def.Responses);
        Assert.Equal(NamesMode.Optional, def.Names);
        Assert.Equal(ResultsVisibility.AfterVote, def.Results); // DESIGN.md §25: voters, while voting, no ballots
        Assert.Equal(ResultsTiming.WhileOpen, def.ResultsTiming);
        Assert.False(def.ShowBallots);
        Assert.Null(def.ClosesOn);
        Assert.Null(def.TimeZone);
        Assert.Equal("", def.DescriptionMarkdown);
        var q = Q(def);
        Assert.Equal(ScoringScheme.Borda, q.Scoring);
        Assert.Equal(0, q.MaxBans);
        Assert.False(q.RankAll);
        Assert.Null(q.Budget);
        Assert.True(q.Maybe);
    }

    [Fact]
    public void Points_settings()
    {
        var draft = Points(100, "A", "B", "C");
        draft.Questions[0].MaxPerOption = 60;
        draft.Questions[0].SpendAll = true;
        var q = Q(Ok(draft));
        Assert.Equal(PollType.Points, q.Type);
        Assert.Equal(100, q.Budget);
        Assert.Equal(60, q.MaxPerOption);
        Assert.True(q.SpendAll);
        Assert.Equal(0, q.MaxBans);
        draft.Questions[0].MaxPerOption = 100;
        Assert.Equal(100, Q(Ok(draft)).MaxPerOption);
    }

    [Fact]
    public void Dates_sorted_and_normalised()
    {
        var draft = DatesDraft(D("2026-11-07", "18:30", "21:00", "Bring snacks"), D("2026-11-03"), D("2026-11-05", "19:00"),
            D("2026-11-10", "08:00"), D("2026-11-11", "08:00"));
        draft.Questions[0].Maybe = false;
        var q = Q(Ok(draft));
        Assert.False(q.Maybe);
        Assert.Equal(["2026-11-03", "2026-11-05 19:00", "2026-11-07 18:30–21:00", "2026-11-10 08:00", "2026-11-11 08:00"],
            q.Options.Select(o => o.Label));
        Assert.Equal("2026-11-07-18-30-21-00", q.Options[2].Key);
        Assert.Equal("Bring snacks", q.Options[2].DescriptionMarkdown);
    }

    [Theory]
    [InlineData("Café crème", "caf-cr-me")]
    [InlineData("  --Hello,  World!--  ", "hello-world")]
    [InlineData("日本", "option")]
    [InlineData("C# night", "c-night")]
    public void Keys_are_ascii_slugs(string label, string key) =>
        Assert.Equal(key, Q(Ok(Ranked(label, "Other"))).Options[0].Key);

    [Fact]
    public void Colliding_slugs_get_numbered()
    {
        var q = Q(Ok(Ranked("A b", "A-b", "a  b!", "Option")));
        Assert.Equal(["a-b", "a-b-2", "a-b-3"], q.Options.Take(3).Select(o => o.Key));
    }

    [Fact]
    public void Default_limits_match_the_design()
    {
        var l = PollLimits.Default;
        Assert.Equal((120, 80, 1000, 2000, 100), (l.TitleMax, l.OptionLabelMax, l.OptionDescriptionMax, l.DescriptionMax, l.OptionsMax));
    }

    [Fact]
    public void Limits_at_the_boundary()
    {
        var limits = new PollLimits(TitleMax: 5, OptionLabelMax: 4, OptionDescriptionMax: 3, DescriptionMax: 6, OptionsMax: 3);
        var d = Ranked("ABCD", "B", "C");
        d.Title = "12345";
        d.Description = "123456";
        d.Questions[0].Options[1].Description = "123";
        Ok(d, limits);
        d.Title = "123456";
        d.Description = "1234567";
        d.Questions[0].Options[0].Label = "ABCDE";
        d.Questions[0].Options[1].Description = "1234";
        var e = Fail(d, limits);
        HasError(e, "title", "now 6");
        HasError(e, "description", "now 7");
        HasError(e, "questions[0].options[0].label", "now 5");
        HasError(e, "questions[0].options[1].description", "now 4");
        // Too many options: the count is the only option error (no per-option work, AV-03).
        d.Questions[0].Options.Add(O("D"));
        e = Fail(d, limits);
        HasError(e, "questions[0].options", "at most 3");
        Assert.DoesNotContain(e, x => x.Field.StartsWith("questions[0].options[", StringComparison.Ordinal));
    }

    [Fact]
    public void Responses_and_timezone_apply_to_points_too()
    {
        var d = Points(5, "A", "B");
        d.Responses = ResponsePolicy.Many;
        d.TimeZone = "America/New_York";
        var def = Ok(d);
        Assert.Equal(ResponsePolicy.Many, def.Responses);
        Assert.Equal("America/New_York", def.TimeZone);
    }

    [Fact]
    public void Timezone_too_long_or_with_whitespace_is_refused()
    {
        var d = Ranked("A", "B");
        d.TimeZone = new string('a', PollRules.TimeZoneMaxLength);
        Ok(d);
        d.TimeZone = new string('a', PollRules.TimeZoneMaxLength + 1);
        HasError(Fail(d), "timezone");
        d.TimeZone = "Europe/ Berlin";
        HasError(Fail(d), "timezone");
    }

    [Fact]
    public void Draft_ignores_label_for_dates_and_other_types_settings()
    {
        var draft = DatesDraft(D("2026-11-03"), D("2026-11-04"));
        draft.Questions[0].Options[0].Label = "ignored";
        draft.Questions[0].Bans = 5;
        draft.Questions[0].Budget = -3;
        draft.Questions[0].MaxPerOption = 0;
        draft.Questions[0].Scoring = (ScoringScheme)99;
        var def = Q(Ok(draft));
        Assert.Equal("2026-11-03", def.Options[0].Label);
        Assert.Equal(0, def.MaxBans);
        Assert.Null(def.Budget);
        Assert.Equal(ScoringScheme.Borda, def.Scoring);
    }

    [Fact]
    public void Ranked_ignores_points_and_dates_fields_and_keeps_defaults()
    {
        var draft = Ranked("A", "B");
        draft.Questions[0].Budget = 10;
        draft.Questions[0].Maybe = false;
        var def = Q(Ok(draft));
        Assert.Null(def.Budget);
        Assert.True(def.Maybe);
    }

    [Fact]
    public void Text_is_trimmed_and_line_endings_normalised()
    {
        var draft = Ranked(" A ", "B");
        draft.Title = "  Hello  ";
        draft.Description = "line1\r\nline2  \r\n\r\n";
        draft.Questions[0].Options[0].Description = "x  \r\ny\n\n";
        var def = Ok(draft);
        Assert.Equal("Hello", def.Title);
        Assert.Equal("line1\nline2", def.DescriptionMarkdown);
        Assert.Equal("A", Q(def).Options[0].Label);
        Assert.Equal("x\ny", Q(def).Options[0].DescriptionMarkdown);
    }

    // ---- errors ----

    [Fact]
    public void Title_required_and_limited()
    {
        var d = Ranked("A", "B");
        d.Title = " ";
        HasError(Fail(d), "title");
        d.Title = new string('x', 11);
        HasError(Fail(d, new PollLimits(TitleMax: 10)), "title", "Shorten the title to 10 characters (now 11).");
    }

    [Fact]
    public void Description_limit()
    {
        var d = Ranked("A", "B");
        d.Description = new string('x', 6);
        HasError(Fail(d, new PollLimits(DescriptionMax: 5)), "description", "now 6");
    }

    [Fact]
    public void Timezone_syntax()
    {
        var d = Ranked("A", "B");
        d.TimeZone = "Not A Zone";
        HasError(Fail(d), "timezone", "Pick a time zone");
        d.TimeZone = "  ";
        Assert.Null(Ok(d).TimeZone);
    }

    [Fact]
    public void Exactly_one_question()
    {
        var d = Ranked("A", "B");
        d.Questions.Add(new QuestionDraft());
        HasError(Fail(d), "questions");
        d.Questions.Clear();
        HasError(Fail(d), "questions");
    }

    [Fact]
    public void Undefined_type_and_enums()
    {
        var d = Ranked("A", "B");
        d.Questions[0].Type = (PollType)42;
        d.VoterMode = (VoterMode)42;
        var e = Fail(d);
        HasError(e, "questions[0].type");
        HasError(e, "voters");
    }

    [Fact]
    public void Points_budget_required_and_cap()
    {
        HasError(Fail(Points(null, "A", "B")), "questions[0].budget");
        HasError(Fail(Points(0, "A", "B")), "questions[0].budget");
        var d = Points(10, "A", "B");
        d.Questions[0].MaxPerOption = 11;
        HasError(Fail(d), "questions[0].maxPerOption", "Lower the cap to the budget (10)");
        d.Questions[0].MaxPerOption = 0;
        HasError(Fail(d), "questions[0].maxPerOption");
    }

    [Fact]
    public void Bans_rules()
    {
        var d = Ranked("A", "B");
        d.Questions[0].Bans = 2;
        HasError(Fail(d), "questions[0].bans", "fewer bans than there are options (2)");
        d.Questions[0].Bans = -1;
        HasError(Fail(d), "questions[0].bans");
        d.Questions[0].Bans = 1;
        Assert.Equal(1, Q(Ok(d)).MaxBans);
    }

    [Fact]
    public void Option_count_rules()
    {
        HasError(Fail(Ranked("A")), "questions[0].options", "at least 2");
        HasError(Fail(Ranked()), "questions[0].options", "now 0");
        HasError(Fail(Ranked("A", "B", "C"), new PollLimits(OptionsMax: 2)), "questions[0].options", "at most 2");
    }

    [Fact]
    public void Option_label_rules()
    {
        var d = Ranked("A", " ", "A", "a", "toolongX");
        var e = Fail(d, new PollLimits(OptionLabelMax: 7));
        HasError(e, "questions[0].options[1].label", "Enter a label");
        HasError(e, "questions[0].options[2].label", "listed twice");
        HasError(e, "questions[0].options[3].label", "listed twice");
        HasError(e, "questions[0].options[4].label", "Shorten this option label to 7");
    }

    [Fact]
    public void Option_description_limit()
    {
        var d = Ranked("A", "B");
        d.Questions[0].Options[1].Description = "123456";
        HasError(Fail(d, new PollLimits(OptionDescriptionMax: 5)), "questions[0].options[1].description", "'B'");
    }

    [Fact]
    public void Dates_errors()
    {
        var d = DatesDraft(
            new OptionDraft(),
            D("2026-11-03"),
            D("2026-11-03"),
            D("2026-11-04", "19:00", "18:00"),
            new OptionDraft { Date = new DateOnly(2026, 11, 5), End = new TimeOnly(20, 0) },
            D("2026-11-06", "18:00", "18:00"));
        var e = Fail(d);
        HasError(e, "questions[0].options[0].date", "Pick a date");
        HasError(e, "questions[0].options[2].date", "'2026-11-03' is listed twice.");
        HasError(e, "questions[0].options[3].end", "end time must be after");
        HasError(e, "questions[0].options[4].end", "start time");
        HasError(e, "questions[0].options[5].end", "end time must be after");
    }

    [Fact]
    public void Dates_same_day_different_slots_are_fine()
    {
        var def = Q(Ok(DatesDraft(D("2026-11-03", "19:00"), D("2026-11-03"), D("2026-11-03", "09:00", "10:00"))));
        Assert.Equal(["2026-11-03", "2026-11-03 09:00–10:00", "2026-11-03 19:00"], def.Options.Select(o => o.Label));
    }

    [Fact]
    public void Errors_collected_together()
    {
        var d = Points(null, "A");
        d.Title = "";
        Assert.True(Fail(d).Count >= 3);
    }

    // ---- edit ----

    [Fact]
    public void Edit_cannot_change_type_or_scoring()
    {
        var existing = Ok(Ranked("A", "B"));
        var pts = Points(5, "A", "B");
        HasError(Fail(pts, existing: existing), "questions[0].type");
        var dow = Ranked("A", "B");
        dow.Questions[0].Scoring = ScoringScheme.Dowdall;
        HasError(Fail(dow, existing: existing), "questions[0].scoring");
        Ok(Ranked("A", "B", "C"), existing: existing);
    }

    [Fact]
    public void Edit_keeps_the_question_key()
    {
        var existing = Ok(Ranked("A", "B"));
        existing = existing with { Questions = [existing.Questions[0] with { Key = "q7" }] };
        Assert.Equal("q7", Q(Ok(Ranked("A", "B"), existing: existing)).Key);
    }

    // ---- results settings (DESIGN.md §25) ----

    private static IReadOnlyList<DraftError> Errors(PollDraft d, PollDefinition? existing = null) =>
        PollDraftValidator.Validate(d, PollLimits.Default, existing).Errors;

    [Theory]
    [InlineData(VoterMode.Invite)]
    [InlineData(VoterMode.Accounts)]
    public void Results_for_anyone_is_refused_for_invite_and_account_polls(VoterMode voters)
    {
        var d = Ranked("A", "B");
        d.VoterMode = voters;
        d.ResultsVisibility = ResultsVisibility.Open;
        Assert.Equal([new DraftError("results", PollDraftValidator.OpenNeedsPublic)], Errors(d));
        d.ResultsVisibility = ResultsVisibility.AfterVote;
        Assert.Empty(Errors(d));
        d.VoterMode = VoterMode.Public;
        d.ResultsVisibility = ResultsVisibility.Open;
        Assert.Empty(Errors(d));
    }

    [Fact]
    public void Show_ballots_needs_names()
    {
        var d = Points(10, "A", "B");
        d.ShowBallots = true;
        d.Names = NamesMode.None;
        Assert.Equal([new DraftError("showBallots", PollDraftValidator.BallotsNeedNames)], Errors(d));
        foreach (var names in new[] { NamesMode.Optional, NamesMode.Required })
        {
            d.Names = names;
            Assert.True(Ok(d).ShowBallots);
        }
    }

    [Fact]
    public void After_closing_needs_an_audience()
    {
        var d = DatesDraft(D("2026-11-03"), D("2026-11-04"));
        d.ResultsTiming = ResultsTiming.AfterClose;
        d.ResultsVisibility = ResultsVisibility.Creator;
        Assert.Equal([new DraftError("resultsTiming", PollDraftValidator.AfterCloseNeedsAudience)], Errors(d));
        d.ResultsVisibility = ResultsVisibility.AfterVote;
        Assert.Equal(ResultsTiming.AfterClose, Ok(d).ResultsTiming);
    }

    [Fact]
    public void Undefined_results_values_are_refused()
    {
        var d = Ranked("A", "B");
        d.ResultsTiming = (ResultsTiming)7;
        Assert.Contains(Errors(d), e => e.Field == "resultsTiming");
    }

    // ---- FromDefinition ----

    public static TheoryData<PollDraft> Drafts()
    {
        var ranked = Ranked("A", "B", "C");
        ranked.Description = "Desc";
        ranked.VoterMode = VoterMode.Invite;
        ranked.Responses = ResponsePolicy.Many;
        ranked.Names = NamesMode.None;
        ranked.ResultsVisibility = ResultsVisibility.AfterVote;
        ranked.ResultsTiming = ResultsTiming.AfterClose;
        ranked.ClosesOn = new DateOnly(2027, 1, 2);
        ranked.TimeZone = "UTC";
        ranked.Questions[0].Scoring = ScoringScheme.Dowdall;
        ranked.Questions[0].Bans = 1;
        ranked.Questions[0].RankAll = true;
        ranked.Questions[0].Options[0].Description = "d1\nd2";
        var points = Points(50, "A", "B");
        points.Questions[0].MaxPerOption = 20;
        points.Questions[0].SpendAll = true;
        points.ResultsVisibility = ResultsVisibility.Open;
        points.ShowBallots = true;
        var dates = DatesDraft(D("2026-11-03", "18:00", "19:00", "desc"), D("2026-11-04", "18:00", "19:00", "desc"), D("2026-11-01"));
        dates.Questions[0].Maybe = false;
        return [ranked, points, dates];
    }

    [Theory]
    [MemberData(nameof(Drafts))]
    public void FromDefinition_round_trips(PollDraft draft)
    {
        var def = Ok(draft);
        var again = Ok(PollDraft.FromDefinition(def));
        AssertSame(def, again);
        var viaCopy = Ok(PollDraft.FromDefinition(again), existing: def);
        AssertSame(def, viaCopy);
    }

    [Fact]
    public void FromDefinition_copies_are_independent()
    {
        var def = Ok(Ranked("A", "B"));
        var a = PollDraft.FromDefinition(def);
        a.Questions[0].Options[0].Label = "Z";
        Assert.Equal("A", PollDraft.FromDefinition(def).Questions[0].Options[0].Label);
    }

    [Fact]
    public void Fifty_thousand_options_are_one_count_error_before_any_option_work()
    {
        var draft = Ranked(Enumerable.Range(0, 50_000).Select(_ => "same").ToArray());
        draft.Questions[0].Options[7].Label = new string('x', 10_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var r = PollDraftValidator.Validate(draft, PollLimits.Default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var e = Assert.Single(r.Errors);
        Assert.Equal("questions[0].options", e.Field);
        Assert.Equal("Remove options: at most 100 are allowed (now 50000).", e.Message);
        Assert.True(allocated < 256 * 1024, $"{allocated} bytes");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), sw.Elapsed.ToString());
    }

    [Fact]
    public void Option_errors_are_capped()
    {
        var draft = Ranked(Enumerable.Range(0, 100).Select(_ => "").ToArray());
        var r = PollDraftValidator.Validate(draft, PollLimits.Default);
        Assert.Equal(PollDraftValidator.MaxErrors, r.Errors.Count(e => e.Field.Contains("].label", StringComparison.Ordinal)));
        Assert.Contains(r.Errors, e => e.Field == "questions[0].options" && e.Message == "Fix the options above; 50 more were not checked.");
    }

    [Fact]
    public void Close_dates_more_than_100_years_ahead_are_refused_option_dates_span_0001_to_9999()
    {
        var today = new DateOnly(2026, 10, 31);
        var d = DatesDraft(D("2126-10-31"), D("2026-11-01"));
        d.ClosesOn = new DateOnly(2126, 10, 31);
        Assert.True(PollDraftValidator.Validate(d, PollLimits.Default, today: today).Success);

        // DESIGN.md §27: the 100-year limit (AV-14) is for the close date only; option dates are wall-clock days.
        d.ClosesOn = new DateOnly(9999, 12, 30);
        d.Questions[0].Options[0].Date = new DateOnly(9999, 12, 31);
        var e = PollDraftValidator.Validate(d, PollLimits.Default, today: today).Errors;
        Assert.Equal([new DraftError("closesOn", "Pick a date on or before 2126-10-31.")], e);

        // Without a day the limit counts from today (UTC).
        Assert.Contains(PollDraftValidator.Validate(d, PollLimits.Default).Errors, x => x.Field == "closesOn");
        Assert.Equal(DateOnly.MaxValue, PollDraftValidator.LatestDate(new DateOnly(9950, 1, 1)));
    }

    [Fact]
    public void Option_dates_at_the_ends_of_the_calendar_are_valid_and_sorted()
    {
        var d = DatesDraft(D("9999-12-31", "23:00", "23:59"), D("0001-01-01", "00:00"), D("9999-12-31"), D("0001-01-01"));
        var q = Q(Ok(d));
        Assert.Equal(["0001-01-01", "0001-01-01 00:00", "9999-12-31", "9999-12-31 23:00–23:59"], q.Options.Select(o => o.Label));
        Assert.Equal(["0001-01-01", "0001-01-01-00-00", "9999-12-31", "9999-12-31-23-00-23-59"], q.Options.Select(o => o.Key));
    }

    [Fact]
    public void Dates_questions_are_capped_at_128_options_under_the_options_limit()
    {
        static PollDraft Days(int n) => DatesDraft(Enumerable.Range(0, n).Select(i => new OptionDraft { Date = new DateOnly(2027, 1, 1).AddDays(i) }).ToArray());
        var high = new PollLimits(OptionsMax: 500);
        Assert.Equal(128, PollRules.MaxDateOptions);
        Assert.Equal(128, PollRules.OptionsLimit(PollType.Dates, high));
        Assert.Equal(500, PollRules.OptionsLimit(PollType.Ranked, high));
        Assert.Equal(100, PollRules.OptionsLimit(PollType.Dates, PollLimits.Default));

        Assert.True(PollDraftValidator.Validate(Days(128), high).Success);
        HasError(Fail(Days(129), high), "questions[0].options", "at most 128");
        HasError(Fail(Days(101), PollLimits.Default), "questions[0].options", "at most 100");
        // Other types keep the admin's limit.
        Assert.True(PollDraftValidator.Validate(Ranked(Enumerable.Range(0, 200).Select(i => $"O{i}").ToArray()), high).Success);
    }
}
