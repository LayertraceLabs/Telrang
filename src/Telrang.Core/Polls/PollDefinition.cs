namespace Telrang.Core.Polls;

/// <summary>One votable option. <see cref="Key"/> is a stable lowercase ASCII slug.</summary>
public sealed record PollOption(string Key, string Label, string DescriptionMarkdown)
{
    /// <summary>Dates questions only (null for the other types): the day, and the optional wall-clock start and end.</summary>
    public DateOnly? Date { get; init; }
    public TimeOnly? Start { get; init; }
    public TimeOnly? End { get; init; }
}

/// <summary>A page of a poll (DESIGN.md §34.2): <see cref="Key"/> is stable (<c>p1</c>, …); the description is Markdown.</summary>
public sealed record PollPage(string Key, string Title, string DescriptionMarkdown);

/// <summary>A titled box of questions on one page (DESIGN.md §34.2): <see cref="Key"/> is stable (<c>g1</c>, …).</summary>
public sealed record PollGroup(string Key, string Title, string DescriptionMarkdown);

/// <summary>
/// One question of a poll (DESIGN.md §20.1, §34.3): its type, the type's settings and its options. <see cref="Key"/> is stable
/// (<c>q1</c>, …) and keys the question's part of every ballot payload. Settings of the other types keep their defaults.
/// </summary>
public sealed record PollQuestion(string Key, PollType Type, IReadOnlyList<PollOption> Options)
{
    /// <summary>Ranked: the scoring scheme (immutable after creation). Other types carry the default.</summary>
    public ScoringScheme Scoring { get; init; } = ScoringScheme.Borda;

    /// <summary>Ranked: most bans per voter, 0 = none.</summary>
    public int MaxBans { get; init; }

    /// <summary>Ranked: every option must be ranked or banned.</summary>
    public bool RankAll { get; init; }

    /// <summary>Points: points per voter (required for points, null otherwise).</summary>
    public int? Budget { get; init; }

    /// <summary>Points: optional cap per option.</summary>
    public int? MaxPerOption { get; init; }

    /// <summary>Points: the whole budget must be used.</summary>
    public bool SpendAll { get; init; }

    /// <summary>Dates: may voters answer "maybe"?</summary>
    public bool Maybe { get; init; } = true;

    /// <summary>
    /// How many option keys edits have issued (DESIGN.md §26, §31). Keys made at creation are slugs of the labels; every
    /// option an edit adds gets <c>_</c> + the next number (<see cref="PollRules.EditKey"/>), which no slug can be, so a key
    /// is never reused: re-adding a removed label starts from zero instead of reviving the votes stored ballots still hold
    /// for the old key. Definitions stored before §31 carried the removed keys as a list (<c>retiredKeys</c>); it is ignored
    /// on read (no edit-time key exists in them) and dropped on the next save.
    /// </summary>
    public int KeysIssued { get; init; }

    // ---- DESIGN.md §34.2: every question ----
    // Definitions stored before §34 have none of these; the defaults reproduce them exactly (no prompt: the poll title is
    // the question; not required: an empty ballot is accepted; no page, no group).

    /// <summary>The question text. May be empty only when the poll has one question (the poll title is the question then).</summary>
    public string Prompt { get; init; } = "";

    /// <summary>Optional Markdown shown with the question.</summary>
    public string HelpMarkdown { get; init; } = "";

    /// <summary>
    /// An answer is needed (DESIGN.md §34.3): ranked = at least one option ranked; points = more than 0 points spent; dates =
    /// at least one yes or maybe; choice = a selection; text = a non-blank text; number = a number. Fixed at creation.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>The key of the <see cref="PollPage"/> the question is on; null in a definition without pages (one implicit page).</summary>
    public string? Page { get; init; }

    /// <summary>The key of the <see cref="PollGroup"/> the question is in, or null.</summary>
    public string? Group { get; init; }

    // ---- Choice (DESIGN.md §34.3; one type for multiple choice and checkboxes) ----

    /// <summary>Choice: fewest selections of an answered question (1 or more; the Other answer counts as one).</summary>
    public int MinSelect { get; init; } = 1;

    /// <summary>Choice: most selections; 1 = radio buttons or a dropdown, more = checkboxes.</summary>
    public int MaxSelect { get; init; } = 1;

    /// <summary>Choice: offers "Other" with a free text (kept apart from the scored payload, DESIGN.md §34.3).</summary>
    public bool AllowOther { get; init; }

    /// <summary>Choice: buttons or dropdown (dropdown only with <see cref="MaxSelect"/> 1).</summary>
    public ChoiceDisplay Display { get; init; } = ChoiceDisplay.Buttons;

    // ---- Text ----

    /// <summary>Text: a multi-line box instead of a single line.</summary>
    public bool Multiline { get; init; }

    /// <summary>Text: fewest characters of a given answer (null = no minimum).</summary>
    public int? MinLength { get; init; }

    /// <summary>Text: most characters; made at creation (the admin's limit when the draft leaves it empty).</summary>
    public int? MaxLength { get; init; }

    // ---- Number ----

    /// <summary>Number: smallest accepted value (null = none).</summary>
    public decimal? Min { get; init; }

    /// <summary>Number: largest accepted value (null = none).</summary>
    public decimal? Max { get; init; }

    /// <summary>Number: most decimal places (0 to <see cref="PollRules.MaxDecimals"/>).</summary>
    public int Decimals { get; init; }

    /// <summary>Number: unit shown after the value (at most <see cref="PollRules.UnitMax"/> characters).</summary>
    public string Unit { get; init; } = "";
}

/// <summary>
/// Validated, immutable poll definition (made by <see cref="PollDraftValidator"/>). Poll-wide settings live here, the
/// type, its settings and options in <see cref="Questions"/> (single-question pages read <c>Questions[0]</c>).
/// <para><see cref="ClosesOn"/>: the poll closes at the end of that day in <see cref="TimeZone"/>; Core has no time zone
/// database, so the instant is computed by the host.</para>
/// <para><see cref="TimeZone"/> is an IANA id, checked only syntactically here (the host validates it against its zone
/// database). Null when the draft leaves it empty (the host fills a default).</para>
/// </summary>
public sealed record PollDefinition(string Title, string DescriptionMarkdown, IReadOnlyList<PollQuestion> Questions)
{
    public ResultsVisibility Results { get; init; } = ResultsVisibility.Creator;

    /// <summary>When the results show (DESIGN.md §25); missing in definitions stored before it = while voting.</summary>
    public ResultsTiming ResultsTiming { get; init; } = ResultsTiming.WhileOpen;

    /// <summary>Voters who see the results also see one row per response: the name as entered and the answers (§25).</summary>
    public bool ShowBallots { get; init; }
    public VoterMode Voters { get; init; } = VoterMode.Public;
    public NamesMode Names { get; init; } = NamesMode.Optional;
    public ResponsePolicy Responses { get; init; } = ResponsePolicy.One;
    public DateOnly? ClosesOn { get; init; }
    public string? TimeZone { get; init; }

    /// <summary>
    /// The pages in order (DESIGN.md §34.2). Empty = one implicit page holding every question (every definition stored
    /// before §34); then no question names a page.
    /// </summary>
    public IReadOnlyList<PollPage> Pages { get; init; } = [];

    /// <summary>The groups (DESIGN.md §34.2), in the order of their first question.</summary>
    public IReadOnlyList<PollGroup> Groups { get; init; } = [];

    /// <summary>
    /// How many question keys were ever issued; keys an edit adds are numbered from here and never reused (DESIGN.md §34.2).
    /// Definitions stored before §34 have 0: <see cref="PollRules.IssuedSoFar"/> then reads the highest key they hold.
    /// </summary>
    public int QuestionsIssued { get; init; }

    /// <summary>How many page keys were ever issued (see <see cref="QuestionsIssued"/>).</summary>
    public int PagesIssued { get; init; }

    /// <summary>How many group keys were ever issued (see <see cref="QuestionsIssued"/>).</summary>
    public int GroupsIssued { get; init; }

    /// <summary>The key of question number <paramref name="index"/> (0-based): <c>q1</c>, <c>q2</c>, …</summary>
    public static string QuestionKey(int index) => $"q{index + 1}";
}
