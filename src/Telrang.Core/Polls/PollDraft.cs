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
/// A page in the form (DESIGN.md §34.2). <see cref="Key"/> is the draft's own reference to the page, which questions name in
/// <see cref="QuestionDraft.Page"/>: the stored key of a page being edited, otherwise any text the editor picks that no
/// other page of the draft uses. The validator keeps the key of a stored page the draft names and issues a new one to every
/// other page, so a removed page's key never comes back (a draft key the poll does not have is just a new page).
/// </summary>
public sealed record PageDraft
{
    public string? Key { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>A group in the form; <see cref="Key"/> works as <see cref="PageDraft.Key"/>, questions name it in <see cref="QuestionDraft.Group"/>.</summary>
public sealed record GroupDraft
{
    public string? Key { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>
/// One question in the form. Settings of the other types are kept (the form remembers them) and ignored by
/// <see cref="PollDraftValidator"/>.
/// </summary>
public sealed record QuestionDraft
{
    /// <summary>
    /// The stored question this row edits (DESIGN.md §26, §34.2), null for a new one; like <see cref="OptionDraft.Key"/>. A key
    /// the poll does not have is ignored. A draft of the only question of a one-question poll without a key is that
    /// question (browser drafts and callers from before §34 have no keys). On create it is ignored.
    /// </summary>
    public string? Key { get; set; }
    /// <summary>The question text (DESIGN.md §34.2); may be empty when the poll has one question.</summary>
    public string Prompt { get; set; } = "";
    public string Help { get; set; } = "";
    public bool Required { get; set; }
    /// <summary>The <see cref="PageDraft.Key"/> of the page the question is on; null when the poll has no pages.</summary>
    public string? Page { get; set; }
    /// <summary>The <see cref="GroupDraft.Key"/> of the group the question is in, or null.</summary>
    public string? Group { get; set; }
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
    // choice
    public int MinSelect { get; set; } = 1;
    public int MaxSelect { get; set; } = 1;
    public bool AllowOther { get; set; }
    public ChoiceDisplay Display { get; set; } = ChoiceDisplay.Buttons;
    // text
    public bool Multiline { get; set; }
    public int? MinLength { get; set; }
    /// <summary>Empty = the admin's limit.</summary>
    public int? MaxLength { get; set; }
    // number
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int Decimals { get; set; }
    public string Unit { get; set; } = "";
    public List<OptionDraft> Options { get; set; } = [];

    /// <summary>The form model of a stored question.</summary>
    public static QuestionDraft FromQuestion(PollQuestion q)
    {
        ArgumentNullException.ThrowIfNull(q);
        return new QuestionDraft
        {
            Key = q.Key,
            Prompt = q.Prompt,
            Help = q.HelpMarkdown,
            Required = q.Required,
            Page = q.Page,
            Group = q.Group,
            MinSelect = q.MinSelect,
            MaxSelect = q.MaxSelect,
            AllowOther = q.AllowOther,
            Display = q.Display,
            Multiline = q.Multiline,
            MinLength = q.MinLength,
            MaxLength = q.MaxLength,
            Min = q.Min,
            Max = q.Max,
            Decimals = q.Decimals,
            Unit = q.Unit,
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

/// <summary>The poll form model (DESIGN.md §20.1, §34).</summary>
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
    public List<PageDraft> Pages { get; set; } = [];
    public List<GroupDraft> Groups { get; set; } = [];
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
            Pages = d.Pages.Select(p => new PageDraft { Key = p.Key, Title = p.Title, Description = p.DescriptionMarkdown }).ToList(),
            Groups = d.Groups.Select(g => new GroupDraft { Key = g.Key, Title = g.Title, Description = g.DescriptionMarkdown }).ToList(),
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
