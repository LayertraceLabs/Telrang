using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Scoring;

namespace Telrang.Core.Tests;

/// <summary>DESIGN.md §26: fixed settings, stable option keys, removed options ignored on read, the change log's diff.</summary>
public class EditRulesTests
{
    private static PollDraft Ranked(params string[] labels) => new()
    {
        Title = "T",
        Questions = [new QuestionDraft { Type = PollType.Ranked, Bans = 1, Options = labels.Select(l => new OptionDraft { Label = l }).ToList() }],
    };

    private static DraftResult Validate(PollDraft d, PollDefinition? existing = null) =>
        PollDraftValidator.Validate(d, PollLimits.Default, existing);

    private static PollDefinition Ok(PollDraft d, PollDefinition? existing = null)
    {
        var r = Validate(d, existing);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return r.Definition!;
    }

    private static PollDraft Edit(PollDefinition def, Action<PollDraft> change)
    {
        var d = PollDraft.FromDefinition(def);
        change(d);
        return d;
    }

    private static PollQuestion Q(PollDefinition d) => d.Questions[0];

    // ---- fixed settings ----

    public static TheoryData<string, string> FixedChanges() => new()
    {
        { "voters", "Who votes" },
        { "responses", "Responses" },
        { "names", "Names" },
        { "results", "Results for" },
        { "resultsTiming", "Show results" },
        { "showBallots", "Show ballots to voters" },
        { "timezone", "The time zone" },
        { "questions[0].bans", "Bans per voter" },
        { "questions[0].rankAll", "Rank all" },
    };

    [Theory]
    [MemberData(nameof(FixedChanges))]
    public void A_fixed_setting_is_refused_on_edit(string field, string name)
    {
        var created = Ranked("Pizza", "Pasta", "Salad").With(d => { d.TimeZone = "Europe/Berlin"; d.Names = NamesMode.Optional; });
        var def = Ok(created);
        var changed = Edit(def, d =>
        {
            switch (field)
            {
                case "voters": d.VoterMode = VoterMode.Invite; break;
                case "responses": d.Responses = ResponsePolicy.Many; break;
                case "names": d.Names = NamesMode.Required; break;
                case "results": d.ResultsVisibility = ResultsVisibility.Creator; break;
                case "resultsTiming": d.ResultsTiming = ResultsTiming.AfterClose; break;
                case "showBallots": d.ShowBallots = true; break;
                case "timezone": d.TimeZone = "Europe/Paris"; break;
                case "questions[0].bans": d.Questions[0].Bans = 0; break;
                case "questions[0].rankAll": d.Questions[0].RankAll = true; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        });
        var errors = Validate(changed, def).Errors;
        Assert.Contains(new DraftError(field, PollDraftValidator.FixedMessage(name)), errors);
        Assert.True(Validate(changed).Success, "the same draft is fine for a new poll");
    }

    [Fact]
    public void Fixed_points_and_dates_rules_are_refused_on_edit()
    {
        var points = Ok(new PollDraft { Title = "P", Questions = [new QuestionDraft { Type = PollType.Points, Budget = 10, Options = [new() { Label = "A" }, new() { Label = "B" }] }] });
        Assert.Contains(new DraftError("questions[0].budget", PollDraftValidator.FixedMessage("The budget")), Validate(Edit(points, d => d.Questions[0].Budget = 20), points).Errors);
        Assert.Contains(new DraftError("questions[0].maxPerOption", PollDraftValidator.FixedMessage("The cap per option")), Validate(Edit(points, d => d.Questions[0].MaxPerOption = 5), points).Errors);
        Assert.Contains(new DraftError("questions[0].spendAll", PollDraftValidator.FixedMessage("Spend all")), Validate(Edit(points, d => d.Questions[0].SpendAll = true), points).Errors);
        // The other types' settings the form remembers are not part of a points poll.
        Assert.True(Validate(Edit(points, d => { d.Questions[0].Bans = 3; d.Questions[0].Maybe = false; }), points).Success);

        var dates = Ok(new PollDraft { Title = "D", Questions = [new QuestionDraft { Type = PollType.Dates, Options = [new() { Date = new DateOnly(2026, 11, 3) }, new() { Date = new DateOnly(2026, 11, 4) }] }] });
        Assert.Contains(new DraftError("questions[0].maybe", PollDraftValidator.FixedMessage("Allow maybe")), Validate(Edit(dates, d => d.Questions[0].Maybe = false), dates).Errors);
    }

    [Fact]
    public void Title_description_options_and_close_date_change_on_edit()
    {
        var def = Ok(Ranked("Pizza", "Pasta", "Salad"));
        var edited = Ok(Edit(def, d =>
        {
            d.Title = "New title";
            d.Description = "More.";
            d.ClosesOn = new DateOnly(2030, 1, 1);
            d.Questions[0].Options.Add(new OptionDraft { Label = "Soup" });
            d.Questions[0].Options.Reverse();
        }), def);
        Assert.Equal(("New title", "More.", new DateOnly(2030, 1, 1)), (edited.Title, edited.DescriptionMarkdown, edited.ClosesOn));
        Assert.Equal(["_1", "salad", "pasta", "pizza"], Q(edited).Options.Select(o => o.Key));
        // An empty time zone keeps the stored one (the host fills it in).
        Assert.True(Validate(Edit(Ok(Ranked("A", "B").With(d => d.TimeZone = "Europe/Berlin")), d => d.TimeZone = null), Ok(Ranked("A", "B").With(d => d.TimeZone = "Europe/Berlin"))).Success);
    }

    [Fact]
    public void Removing_options_down_to_the_bans_names_the_options()
    {
        var def = Ok(Ranked("Pizza", "Pasta", "Salad").With(d => d.Questions[0].Bans = 2));
        var errors = Validate(Edit(def, d => d.Questions[0].Options.RemoveAt(2)), def).Errors;
        Assert.Contains(new DraftError("questions[0].options", PollRules.KeepMoreOptionsThanBans(2)), errors);
        Assert.DoesNotContain(errors, e => e.Field == "questions[0].bans");
    }

    // ---- stable keys ----

    [Fact]
    public void A_renamed_option_keeps_its_key()
    {
        var def = Ok(Ranked("Pizza", "Pasta"));
        var renamed = Ok(Edit(def, d => d.Questions[0].Options[0].Label = "Pizza Napoli"), def);
        Assert.Equal(["pizza", "pasta"], Q(renamed).Options.Select(o => o.Key));
        Assert.Equal("Pizza Napoli", Q(renamed).Options[0].Label);
        Assert.Equal(0, Q(renamed).KeysIssued);
    }

    [Fact]
    public void A_dates_option_with_its_key_keeps_it_when_its_date_moves()
    {
        var def = Ok(new PollDraft { Title = "D", Questions = [new QuestionDraft { Type = PollType.Dates, Options = [new() { Date = new DateOnly(2026, 11, 3) }, new() { Date = new DateOnly(2026, 11, 4) }] }] });
        var moved = Ok(Edit(def, d => d.Questions[0].Options[0].Date = new DateOnly(2026, 11, 5)), def);
        Assert.Equal(["2026-11-04", "2026-11-03"], Q(moved).Options.Select(o => o.Key)); // sorted by date, keys kept
        Assert.Equal("2026-11-05", Q(moved).Options[1].Label);
    }

    [Fact]
    public void An_option_without_a_key_takes_the_key_of_the_same_label()
    {
        var def = Ok(Ranked("Pizza", "Pasta"));
        // Text mode, the calendar, or remove and add again before saving: no keys, the labels match.
        var fromText = Ok(Edit(def, d => d.Questions[0].Options = [new() { Label = "Pasta" }, new() { Label = "Pizza" }]), def);
        Assert.Equal(["pasta", "pizza"], Q(fromText).Options.Select(o => o.Key));
        Assert.Equal(0, Q(fromText).KeysIssued);
        // Case counts: a different label is a new option.
        var other = Ok(Edit(def, d => d.Questions[0].Options = [new() { Label = "pizza" }, new() { Label = "Pasta" }]), def);
        Assert.Equal(["_1", "pasta"], Q(other).Options.Select(o => o.Key));
        Assert.Equal(1, Q(other).KeysIssued);
    }

    [Fact]
    public void Keys_are_never_reused()
    {
        var def = Ok(Ranked("Pizza", "Pasta", "Salad"));
        var removed = Ok(Edit(def, d => d.Questions[0].Options.RemoveAt(0)), def);
        Assert.Equal(["pasta", "salad"], Q(removed).Options.Select(o => o.Key));
        Assert.Equal(0, Q(removed).KeysIssued);

        // Re-adding the removed label starts from zero: a new key, never the removed one (DESIGN.md §31: numbered).
        var readded = Ok(Edit(removed, d => d.Questions[0].Options.Add(new OptionDraft { Label = "Pizza" })), removed);
        Assert.Equal(["pasta", "salad", "_1"], Q(readded).Options.Select(o => o.Key));
        Assert.Equal(1, Q(readded).KeysIssued);

        // And again: the counter only grows, so the next one is _2.
        var again = Ok(Edit(readded, d => d.Questions[0].Options.RemoveAt(2)), readded);
        Assert.Equal(1, Q(again).KeysIssued);
        var third = Ok(Edit(again, d => d.Questions[0].Options.Add(new OptionDraft { Label = "Pizza" })), again);
        Assert.Equal("_2", Q(third).Options[^1].Key);

        // A renamed option does not free its old label's slug for a new option.
        var renamed = Ok(Edit(def, d =>
        {
            d.Questions[0].Options[0].Label = "Calzone";
            d.Questions[0].Options.Add(new OptionDraft { Label = "Pizza!" });
        }), def);
        Assert.Equal(["pizza", "pasta", "salad", "_1"], Q(renamed).Options.Select(o => o.Key));
    }

    [Fact]
    public void A_definition_stored_with_retired_keys_reads_without_them_and_never_meets_their_keys()
    {
        // Before §31 the removed keys were stored as a list; edit keys are numbered and no slug holds "_".
        var def = Ok(Ranked("Pizza", "Pasta", "Salad"));
        var edited = Ok(Edit(def, d =>
        {
            d.Questions[0].Options.RemoveAt(0);
            d.Questions[0].Options.Add(new OptionDraft { Label = "Pizza 2" });
            d.Questions[0].Options.Add(new OptionDraft { Label = "_1" });
        }), def);
        Assert.Equal(["pasta", "salad", "_1", "_2"], Q(edited).Options.Select(o => o.Key));
        Assert.Equal("1", PollRules.Slugify("_1"));
        Assert.DoesNotContain('_', PollRules.Slugify("a_b _c_"));
    }

    [Fact]
    public void Foreign_and_duplicate_keys_are_ignored()
    {
        var def = Ok(Ranked("Pizza", "Pasta", "Salad"));
        var removed = Ok(Edit(def, d => d.Questions[0].Options.RemoveAt(0)), def);
        // A browser draft naming a retired key or a key the poll never had: treated as a new option.
        var forged = Ok(Edit(removed, d =>
        {
            d.Questions[0].Options.Add(new OptionDraft { Key = "pizza", Label = "Pizza" });
            d.Questions[0].Options.Add(new OptionDraft { Key = "other", Label = "Soup" });
            d.Questions[0].Options.Add(new OptionDraft { Key = "pasta", Label = "Pasta again" });
        }), removed);
        Assert.Equal(["pasta", "salad", "_1", "_2", "_3"], Q(forged).Options.Select(o => o.Key));

        // On create keys are ignored entirely (a copy of another poll carries them).
        var created = Ok(new PollDraft { Title = "T", Questions = [new QuestionDraft { Options = [new() { Key = "zzz", Label = "A" }, new() { Key = "zzz", Label = "B" }] }] });
        Assert.Equal(["a", "b"], Q(created).Options.Select(o => o.Key));
        Assert.Equal(0, Q(created).KeysIssued);
    }

    // ---- change log diff ----

    [Fact]
    public void Changes_between_two_versions()
    {
        var def = Ok(Ranked("Pizza", "Pasta", "Salad"));
        var after = Ok(Edit(def, d =>
        {
            d.Questions[0].Options.RemoveAt(2);                 // Salad removed
            d.Questions[0].Options[0].Label = "Calzone";        // renamed
            d.Questions[0].Options[1].Label = " PASTA ";        // case and spacing only: not recorded
            d.Questions[0].Options[1].Description = "Al dente"; // details
            d.Questions[0].Options.Add(new OptionDraft { Label = "Soup" });
            d.Questions[0].Options.Reverse();                   // reorder: not recorded
        }), def);
        Assert.Equal(
            [
                new OptionChange(OptionChangeKind.Removed, "salad", "Salad"),
                new OptionChange(OptionChangeKind.Added, "_1", "Soup"),
                new OptionChange(OptionChangeKind.DetailsChanged, "pasta", "PASTA"),
                new OptionChange(OptionChangeKind.Renamed, "pizza", "Calzone", "Pizza"),
            ],
            OptionChanges.Between(Q(def), Q(after)));
        Assert.Empty(OptionChanges.Between(Q(def), Q(def)));
    }

    [Theory]
    [InlineData("Pizza", "pizza", true)]
    [InlineData("Pizza  Napoli", " pizza\tnapoli", true)]
    [InlineData("Café", "Cafe\u0301", true)] // canonical equivalence (NFC)
    [InlineData("Pizza Napoli", "pizza-napoli!", false)]
    [InlineData("Café", "cafe", false)]
    [InlineData("Pizza 1", "Pizza 2", false)]
    [InlineData("A.B", "AB", false)]
    [InlineData("Fee +10%", "Fee -10%", false)]
    [InlineData("Plan \u2160", "Plan \u2161", false)]
    [InlineData("Area 10\u00B2", "Area 102", false)]
    public void Only_case_and_whitespace_changes_are_not_renames(string a, string b, bool same) =>
        Assert.Equal(same, OptionChanges.SameText(a, b));

    // ---- removed options ignored on read ----

    [Fact]
    public void Ranked_scoring_skips_removed_options_and_moves_the_rest_up()
    {
        var def = Ok(Ranked("Pizza", "Pasta", "Salad"));
        var after = Q(Ok(Edit(def, d => d.Questions[0].Options.RemoveAt(0)), def)); // Pizza removed: 2 options
        var ballots = new[] { new RankedBallot(["pizza", "pasta", "salad"], []), new RankedBallot(["salad"], ["pizza"]) };
        var r = RankedResults.Compute(after, ballots);
        Assert.Equal(2, r.VoterCount);
        var pasta = r.Rows.Single(x => x.Key == "pasta");
        var salad = r.Rows.Single(x => x.Key == "salad");
        Assert.Equal((2.0, 1), (pasta.Score, pasta.FirstChoiceCount)); // was second, now first: 2 of 2 points
        Assert.Equal((1.0 + 2.0, 1), (salad.Score, salad.FirstChoiceCount));
        Assert.DoesNotContain(r.Rows, x => x.Key == "pizza");
        Assert.All(r.Rows, x => Assert.Equal(0, x.BanCount));
    }

    [Fact]
    public void Points_and_dates_ignore_removed_options_and_read_new_ones_as_empty()
    {
        var points = new PollQuestion("q1", PollType.Points, [new("a", "A", ""), new("new", "New", "")]) { Budget = 10, SpendAll = true };
        var p = PointsResults.Compute(points, [new PointsBallot(new Dictionary<string, int> { ["a"] = 4, ["gone"] = 6 })]);
        Assert.Equal((4L, 0L), (p.Rows.Single(r => r.Key == "a").Total, p.Rows.Single(r => r.Key == "new").Total));
        Assert.Equal(["a"], BallotFilter.Known(points, new PointsBallot(new Dictionary<string, int> { ["a"] = 4, ["gone"] = 6 })).Points.Keys);

        var dates = new PollQuestion("q1", PollType.Dates, [new("d1", "2026-11-03", ""), new("d3", "2026-11-05", "")]);
        var d = DatesResults.Compute(dates, [new DatesBallot(new Dictionary<string, Availability> { ["d1"] = Availability.Yes, ["d2"] = Availability.Yes })]);
        Assert.Equal((1, 0), (d.Rows.Single(r => r.Key == "d1").Yes, d.Rows.Single(r => r.Key == "d3").Yes));
        Assert.Equal(1, d.Rows.Single(r => r.Key == "d3").No);

        var ranked = new PollQuestion("q1", PollType.Ranked, [new("a", "A", ""), new("b", "B", "")]) { MaxBans = 1 };
        Assert.Equal(new[] { "b" }, BallotFilter.Known(ranked, new RankedBallot(["gone", "b", "b"], ["x"])).Ranked);
        Assert.Empty(BallotFilter.Known(ranked, new RankedBallot(["gone", "b"], ["x"])).Banned);
    }

    [Fact]
    public void A_new_option_under_rank_all_must_be_ranked_and_then_the_ballot_is_valid()
    {
        var def = Ok(Ranked("Pizza", "Pasta").With(d => d.Questions[0].RankAll = true));
        var after = Q(Ok(Edit(def, d => d.Questions[0].Options.Add(new OptionDraft { Label = "Soup" })), def));
        var old = new RankedBallot(["pizza", "pasta"], []);
        Assert.Contains("Option 'Soup' must be ranked or banned.", BallotValidator.Validate(after, old));
        Assert.Empty(BallotValidator.Validate(after, new RankedBallot(["pizza", "pasta", "_1"], [])));
        Assert.Empty(BallotValidator.Validate(after, new RankedBallot(["pizza", "pasta"], ["_1"])));
    }

    // ---- hidden characters in labels (DESIGN.md §31, pentest R3-VOTE-1) ----

    [Theory]
    [InlineData("Pay \u202E15")]   // right-to-left override (Cf)
    [InlineData("Pay\u200B15")]    // zero-width space (Cf)
    [InlineData("A\u2066B\u2069")] // isolates (Cf)
    [InlineData("Tab\tin")]        // control (Cc)
    [InlineData("Private \uE000")] // private use (Co)
    public void Labels_refuse_control_format_private_and_surrogate_characters(string label)
    {
        var result = Validate(Ranked(label, "Other"));
        Assert.Contains(new DraftError("questions[0].options[0].label", PollRules.HiddenCharacters), result.Errors);
        var def = Ok(Ranked("Pay 15", "Other"));
        Assert.Contains(new DraftError("questions[0].options[0].label", PollRules.HiddenCharacters),
            Validate(Edit(def, d => d.Questions[0].Options[0].Label = label), def).Errors);
    }

    [Fact]
    public void A_lone_surrogate_is_refused() // not as InlineData: the test runner replaces it on the way
    {
        Assert.True(PollRules.HasHiddenCharacters("Lone " + (char)0xD800));
        Assert.True(PollRules.HasHiddenCharacters((char)0xDC00 + "x"));
    }

    [Theory]
    [InlineData("Pizza 🍕")]
    [InlineData("Café")]
    [InlineData("שלום")]
    [InlineData("Fee +10% ✓")]
    public void Ordinary_labels_pass_the_hidden_character_check(string label) =>
        Assert.False(PollRules.HasHiddenCharacters(label));

    [Fact]
    public void Title_and_description_changes_are_recorded_except_case_and_whitespace()
    {
        var def = Ok(Ranked("Pizza", "Pasta").With(d => { d.Title = "Which should we do?"; d.Description = "Pick one."; }));
        Assert.Empty(OptionChanges.Between(def, Ok(Edit(def, d => { d.Title = "which  should we DO?"; d.Description = "pick  ONE."; }), def)));
        Assert.Equal(
            [
                new OptionChange(OptionChangeKind.TitleChanged, "", "Which should we NOT do?", "Which should we do?"),
                new OptionChange(OptionChangeKind.DescriptionChanged, "", ""),
            ],
            OptionChanges.Between(def, Ok(Edit(def, d => { d.Title = "Which should we NOT do?"; d.Description = "Pick none."; }), def)));
    }
}

internal static class DraftExtensions
{
    public static PollDraft With(this PollDraft d, Action<PollDraft> change)
    {
        change(d);
        return d;
    }
}
