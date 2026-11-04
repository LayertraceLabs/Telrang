namespace Telrang.Core.Polls;

/// <summary>One votable option. <see cref="Key"/> is a stable lowercase ASCII slug.</summary>
public sealed record PollOption(string Key, string Label, string DescriptionMarkdown)
{
    /// <summary>Dates questions only (null for the other types): the day, and the optional wall-clock start and end.</summary>
    public DateOnly? Date { get; init; }
    public TimeOnly? Start { get; init; }
    public TimeOnly? End { get; init; }
}

/// <summary>
/// One question of a poll (DESIGN.md §20.1): its type, the type's settings and its options. <see cref="Key"/> is stable
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
}

/// <summary>
/// Validated, immutable poll definition (made by <see cref="PollDraftValidator"/>). Poll-wide settings live here, the
/// type, its settings and options in <see cref="Questions"/> (exactly one for now; pages read <c>Questions[0]</c>).
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

    /// <summary>The key of question number <paramref name="index"/> (0-based): <c>q1</c>, <c>q2</c>, …</summary>
    public static string QuestionKey(int index) => $"q{index + 1}";
}
