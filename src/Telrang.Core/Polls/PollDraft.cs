namespace Telrang.Core.Polls;

/// <summary>One option in the form. Dates polls use <see cref="Date"/>/<see cref="Start"/>/<see cref="End"/> (the label is derived).</summary>
public sealed record OptionDraft
{
    /// <summary>
    /// The stored option this row edits (DESIGN.md §26), null for a new one. On edit the validator keeps it, so a label or
    /// date change keeps the option and its votes; a key the poll does not have is ignored. On create it is ignored.
    /// </summary>
    public string? Key { get; set; }
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly? Date { get; set; }
    public TimeOnly? Start { get; set; }
    public TimeOnly? End { get; set; }
}

/// <summary>
/// One question in the form. Settings of the other types are kept (the form remembers them) and ignored by
/// <see cref="PollDraftValidator"/>.
/// </summary>
public sealed record QuestionDraft
{
    public PollType Type { get; set; } = PollType.Ranked;
    // ranked
    public ScoringScheme Scoring { get; set; } = ScoringScheme.Borda;
    public int Bans { get; set; }
    public bool RankAll { get; set; }
    // points
    public int? Budget { get; set; }
    public int? MaxPerOption { get; set; }
    public bool SpendAll { get; set; }
    // dates
    public bool Maybe { get; set; } = true;
    public List<OptionDraft> Options { get; set; } = [];

    /// <summary>The form model of a stored question.</summary>
    public static QuestionDraft FromQuestion(PollQuestion q)
    {
        ArgumentNullException.ThrowIfNull(q);
        return new QuestionDraft
        {
            Type = q.Type,
            Scoring = q.Scoring,
            Bans = q.MaxBans,
            RankAll = q.RankAll,
            Budget = q.Budget,
            MaxPerOption = q.MaxPerOption,
            SpendAll = q.SpendAll,
            Maybe = q.Maybe,
            Options = q.Options.Select(o => new OptionDraft
            {
                Key = o.Key,
                Label = o.Label,
                Description = o.DescriptionMarkdown,
                Date = o.Date,
                Start = o.Start,
                End = o.End,
            }).ToList(),
        };
    }
}

/// <summary>The poll form model (DESIGN.md §20.1). Exactly one question is allowed for now.</summary>
public sealed record PollDraft
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public VoterMode VoterMode { get; set; } = VoterMode.Public;
    public ResponsePolicy Responses { get; set; } = ResponsePolicy.One;
    public NamesMode Names { get; set; } = NamesMode.Optional;
    public ResultsVisibility ResultsVisibility { get; set; } = ResultsVisibility.AfterVote;
    public ResultsTiming ResultsTiming { get; set; } = ResultsTiming.WhileOpen;
    public bool ShowBallots { get; set; }
    public DateOnly? ClosesOn { get; set; }
    public string? TimeZone { get; set; }
    public List<QuestionDraft> Questions { get; set; } = [new QuestionDraft()];

    /// <summary>The form model of an existing definition (edit, "copy from existing poll").</summary>
    public static PollDraft FromDefinition(PollDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new PollDraft
        {
            Title = d.Title,
            Description = d.DescriptionMarkdown,
            VoterMode = d.Voters,
            Responses = d.Responses,
            Names = d.Names,
            ResultsVisibility = d.Results,
            ResultsTiming = d.ResultsTiming,
            ShowBallots = d.ShowBallots,
            ClosesOn = d.ClosesOn,
            TimeZone = d.TimeZone,
            Questions = d.Questions.Select(QuestionDraft.FromQuestion).ToList(),
        };
    }
}

/// <summary>A validation error keyed by field path, e.g. <c>questions[0].options[3].label</c>.</summary>
public sealed record DraftError(string Field, string Message);

public sealed record DraftResult(PollDefinition? Definition, IReadOnlyList<DraftError> Errors)
{
    public bool Success => Definition is not null;
}
