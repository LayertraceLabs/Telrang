namespace Telrang.Core.Polls;

/// <summary>
/// The single source of definition rules (DESIGN.md §20.1): validates a <see cref="PollDraft"/> into a
/// <see cref="PollDefinition"/>, or field-keyed errors. With <c>existing</c> it also applies the edit rules (DESIGN.md §26:
/// every setting fixed, only title, description, options and close date change; question and option keys kept).
/// </summary>
public static class PollDraftValidator
{
    /// <summary>Most option errors reported for one question; the remaining options are not checked (AV-03).</summary>
    public const int MaxErrors = 50;

    /// <summary>
    /// How far ahead a close date may be (AV-14: a close instant near 9999 overflows later arithmetic). Option dates are
    /// wall-clock days never converted to instants and may be anything from 0001-01-01 to 9999-12-31 (DESIGN.md §27).
    /// </summary>
    public const int MaxYearsAhead = 100;

    /// <summary>The latest close date accepted: <paramref name="today"/> + <see cref="MaxYearsAhead"/> years.</summary>
    public static DateOnly LatestDate(DateOnly today) =>
        today.Year <= DateOnly.MaxValue.Year - MaxYearsAhead ? today.AddYears(MaxYearsAhead) : DateOnly.MaxValue;

    public static string DateTooFar(DateOnly latest) => $"Pick a date on or before {latest:yyyy-MM-dd}.";

    /// <summary>The refusal of a change to a setting fixed at creation (DESIGN.md §26).</summary>
    public static string FixedMessage(string setting) => $"{setting} cannot be changed after creation.";

    public const string OpenNeedsPublic = "Pick Owner only or Voters: only polls open to anyone with the link show results to anyone.";
    public const string AfterCloseNeedsAudience = "Pick While voting, or let voters see the results.";
    public const string BallotsNeedNames = "Ask voters for names, or turn off showing ballots.";

    /// <param name="today">The day the 100-year limit on the close date counts from (the poll's creation day; UTC today when null).</param>
    public static DraftResult Validate(PollDraft draft, PollLimits limits, PollDefinition? existing = null, DateOnly? today = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(limits);
        var errors = new List<DraftError>();
        void Err(string field, string message) => errors.Add(new DraftError(field, message));
        var latest = LatestDate(today ?? DateOnly.FromDateTime(DateTime.UtcNow));

        if (draft.ClosesOn is { } closesOn && closesOn > latest) Err("closesOn", DateTooFar(latest));

        var title = (draft.Title ?? "").Trim();
        if (title.Length == 0) Err("title", "Enter a title.");
        else if (title.Length > limits.TitleMax) Err("title", PollRules.TitleTooLong(limits, title.Length));

        var description = Norm(draft.Description).Trim();
        if (description.Length > limits.DescriptionMax) Err("description", PollRules.DescriptionTooLong(limits, description.Length));

        string? timeZone = null;
        var tz = draft.TimeZone?.Trim();
        if (!string.IsNullOrEmpty(tz))
        {
            if (PollRules.TimeZoneOk(tz)) timeZone = tz;
            else Err("timezone", PollRules.InvalidTimeZone(tz));
        }

        if (!System.Enum.IsDefined(draft.VoterMode)) Err("voters", "Choose who may vote.");
        if (!System.Enum.IsDefined(draft.Responses)) Err("responses", "Choose how many responses one voter may give.");
        if (!System.Enum.IsDefined(draft.Names)) Err("names", "Choose whether voters give a name.");
        if (!System.Enum.IsDefined(draft.ResultsVisibility)) Err("results", "Choose who sees the results.");
        else if (draft.ResultsVisibility == ResultsVisibility.Open && draft.VoterMode != VoterMode.Public)
            Err("results", OpenNeedsPublic);
        if (!System.Enum.IsDefined(draft.ResultsTiming)) Err("resultsTiming", "Choose when the results show.");
        else if (draft.ResultsTiming == ResultsTiming.AfterClose && draft.ResultsVisibility == ResultsVisibility.Creator)
            Err("resultsTiming", AfterCloseNeedsAudience);
        if (draft.ShowBallots && draft.Names == NamesMode.None) Err("showBallots", BallotsNeedNames);

        // Edit rules (DESIGN.md §26): every setting but the title, description, options and close date is fixed at creation.
        if (existing is not null)
        {
            if (draft.VoterMode != existing.Voters) Err("voters", FixedMessage("Who votes"));
            if (draft.Responses != existing.Responses) Err("responses", FixedMessage("Responses"));
            if (draft.Names != existing.Names) Err("names", FixedMessage("Names"));
            if (draft.ResultsVisibility != existing.Results) Err("results", FixedMessage("Results for"));
            if (draft.ResultsTiming != existing.ResultsTiming) Err("resultsTiming", FixedMessage("Show results"));
            if (draft.ShowBallots != existing.ShowBallots) Err("showBallots", FixedMessage("Show ballots to voters"));
            // An empty zone keeps the stored one (the host fills it in).
            if (timeZone is not null && existing.TimeZone is not null && timeZone != existing.TimeZone) Err("timezone", FixedMessage("The time zone"));
        }

        if (draft.Questions is null || draft.Questions.Count != 1)
        {
            Err("questions", "A poll needs exactly one question.");
            return new DraftResult(null, errors);
        }

        var questions = new List<PollQuestion>(draft.Questions.Count);
        for (var qi = 0; qi < draft.Questions.Count; qi++)
        {
            var question = ValidateQuestion(draft.Questions[qi], $"questions[{qi}]", limits,
                existing?.Questions.ElementAtOrDefault(qi), existing?.Questions.ElementAtOrDefault(qi)?.Key ?? PollDefinition.QuestionKey(qi), errors);
            if (question is not null) questions.Add(question);
        }

        if (errors.Count > 0) return new DraftResult(null, errors);

        var def = new PollDefinition(title, description, questions)
        {
            Results = draft.ResultsVisibility,
            ResultsTiming = draft.ResultsTiming,
            ShowBallots = draft.ShowBallots,
            Voters = draft.VoterMode,
            Names = draft.Names,
            Responses = draft.Responses,
            ClosesOn = draft.ClosesOn,
            TimeZone = timeZone,
        };
        return new DraftResult(def, errors);
    }

    private static PollQuestion? ValidateQuestion(QuestionDraft q, string path, PollLimits limits, PollQuestion? existing, string key,
        List<DraftError> errors)
    {
        var before = errors.Count;
        void Err(string field, string message) => errors.Add(new DraftError($"{path}.{field}", message));
        if (q is null) { errors.Add(new DraftError(path, "Add the question.")); return null; }

        var typeOk = System.Enum.IsDefined(q.Type);
        if (!typeOk) Err("type", "Choose a poll type.");
        var type = q.Type;

        var scoring = ScoringScheme.Borda;
        var bans = 0;
        var rankAll = false;
        int? budget = null, maxPer = null;
        var spendAll = false;
        var maybe = true;

        if (typeOk)
        {
            switch (type)
            {
                case PollType.Ranked:
                    if (System.Enum.IsDefined(q.Scoring)) scoring = q.Scoring;
                    else Err("scoring", "Choose a scoring scheme.");
                    if (q.Bans < 0) Err("bans", "Enter 0 or more bans.");
                    else bans = q.Bans;
                    rankAll = q.RankAll;
                    break;
                case PollType.Points:
                    if (q.Budget is not int b || b < 1) Err("budget", "Enter a budget of 1 or more.");
                    else budget = b;
                    if (q.MaxPerOption is int mp)
                    {
                        if (mp < 1) Err("maxPerOption", "Enter a cap of 1 or more, or leave it empty.");
                        else if (budget is int bg && mp > bg) Err("maxPerOption", PollRules.MaxPerTooBig(mp, bg));
                        else maxPer = mp;
                    }
                    spendAll = q.SpendAll;
                    break;
                case PollType.Dates:
                    maybe = q.Maybe;
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        // ---- options ----
        var rows = new List<Row>();
        var inputs = q.Options ?? [];
        // A restored browser draft is untrusted: the count is checked before any per-option work (AV-03). Dates questions
        // are capped at PollRules.MaxDateOptions under the shared limit (DESIGN.md §27).
        var optionsMax = PollRules.OptionsLimit(type, limits);
        if (inputs.Count > optionsMax)
        {
            Err("options", PollRules.TooManyOptions(optionsMax, inputs.Count));
            return null;
        }
        var optionErrors = 0;
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSlots = new HashSet<DateSlot>();
        for (var i = 0; i < inputs.Count; i++)
        {
            if (errors.Count - before >= MaxErrors) { optionErrors = inputs.Count - i; break; }
            var o = inputs[i] ?? new OptionDraft();
            string P(string f) => $"options[{i}].{f}";
            var desc = CleanDescription(o.Description);
            if (type == PollType.Dates)
            {
                var ok = true;
                if (o.Date is not DateOnly date) { Err(P("date"), "Pick a date."); continue; }
                if (o.Start is null && o.End is not null) { Err(P("end"), "Add a start time before the end time."); ok = false; }
                else if (o.Start is TimeOnly s && o.End is TimeOnly e && e <= s) { Err(P("end"), PollRules.EndTimeBeforeStart); ok = false; }
                var label = PollRules.DateLabel(date, o.Start, o.End);
                if (ok && !seenSlots.Add(new DateSlot(date, o.Start, o.End))) { Err(P("date"), $"'{label}' is listed twice."); ok = false; }
                if (desc.Length > limits.OptionDescriptionMax)
                {
                    Err(P("description"), PollRules.OptionDescriptionTooLong(limits, label, desc.Length));
                    ok = false;
                }
                if (ok) rows.Add(new Row(o.Key, label, desc, date, o.Start, o.End));
            }
            else
            {
                var label = (o.Label ?? "").Trim();
                var ok = true;
                if (label.Length == 0) { Err(P("label"), "Enter a label."); ok = false; }
                else if (label.Length > limits.OptionLabelMax) { Err(P("label"), PollRules.LabelTooLong(limits, label.Length)); ok = false; }
                else if (PollRules.HasHiddenCharacters(label)) { Err(P("label"), PollRules.HiddenCharacters); ok = false; }
                else if (!seenLabels.Add(label)) { Err(P("label"), $"'{label}' is listed twice."); ok = false; }
                if (desc.Length > limits.OptionDescriptionMax)
                {
                    Err(P("description"), PollRules.OptionDescriptionTooLong(limits, label, desc.Length));
                    ok = false;
                }
                if (ok) rows.Add(new Row(o.Key, label, desc, null, null, null));
            }
        }

        if (optionErrors > 0) Err("options", $"Fix the options above; {optionErrors} more were not checked.");
        if (inputs.Count < 2) Err("options", PollRules.TooFewOptions(inputs.Count));
        if (type == PollType.Ranked && typeOk && bans > 0 && inputs.Count >= 2 && bans >= inputs.Count)
        {
            // On edit the bans are fixed: the options are what has to change.
            if (existing is not null) Err("options", PollRules.KeepMoreOptionsThanBans(bans));
            else Err("bans", PollRules.BansTooMany(bans, inputs.Count));
        }

        // Edit rules (DESIGN.md §26): the type and its rules are fixed at creation.
        if (existing is not null && typeOk)
        {
            if (existing.Type != type) Err("type", "The poll type cannot be changed after creation.");
            else
            {
                switch (type)
                {
                    case PollType.Ranked:
                        if (existing.Scoring != scoring) Err("scoring", "The scoring scheme cannot be changed after creation.");
                        if (existing.MaxBans != q.Bans) Err("bans", FixedMessage("Bans per voter"));
                        if (existing.RankAll != rankAll) Err("rankAll", FixedMessage("Rank all"));
                        break;
                    case PollType.Points:
                        if (existing.Budget != q.Budget) Err("budget", FixedMessage("The budget"));
                        if (existing.MaxPerOption != q.MaxPerOption) Err("maxPerOption", FixedMessage("The cap per option"));
                        if (existing.SpendAll != spendAll) Err("spendAll", FixedMessage("Spend all"));
                        break;
                    case PollType.Dates:
                        if (existing.Maybe != maybe) Err("maybe", FixedMessage("Allow maybe"));
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
        }

        if (errors.Count > before) return null;

        if (type == PollType.Dates)
            rows = rows.OrderBy(r => r.Date).ThenBy(r => r.Start).ThenBy(r => r.End).ToList();
        var (keys, issued) = AssignKeys(rows, existing);
        var options = rows.Select((r, i) => new PollOption(keys[i], r.Label, r.Description)
        {
            Date = r.Date,
            Start = r.Start,
            End = r.End,
        }).ToList();

        return new PollQuestion(key, type, options)
        {
            KeysIssued = issued,
            Scoring = scoring,
            MaxBans = bans,
            RankAll = rankAll,
            Budget = budget,
            MaxPerOption = maxPer,
            SpendAll = spendAll,
            Maybe = maybe,
        };
    }

    private sealed record Row(string? Key, string Label, string Description, DateOnly? Date, TimeOnly? Start, TimeOnly? End);

    /// <summary>
    /// Option keys (DESIGN.md §26, §31). New poll: the slug of each label, made unique. Edit: a row keeps the stored key it
    /// names (once); a row without one takes the key of a stored option with exactly its label (Text mode, the calendar,
    /// a removed and re-added option before saving), when no other row kept that key; every other row gets the next
    /// edit key (<see cref="PollRules.EditKey"/>), counted by <see cref="PollQuestion.KeysIssued"/>, so no key the poll
    /// ever had (current or removed) comes back.
    /// </summary>
    private static (string[] Keys, int Issued) AssignKeys(List<Row> rows, PollQuestion? existing)
    {
        var keys = new string?[rows.Count];
        if (existing is null)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rows.Count; i++) keys[i] = PollRules.UniqueKey(PollRules.Slugify(rows[i].Label), used);
            return (keys.Select(k => k!).ToArray(), 0);
        }
        var stored = existing.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Key is { } k && stored.Contains(k) && kept.Add(k)) keys[i] = k;
        for (var i = 0; i < rows.Count; i++)
        {
            if (keys[i] is not null) continue;
            var match = existing.Options.FirstOrDefault(o => !kept.Contains(o.Key) && string.Equals(o.Label, rows[i].Label, StringComparison.Ordinal));
            if (match is not null && kept.Add(match.Key)) keys[i] = match.Key;
        }
        var issued = existing.KeysIssued;
        for (var i = 0; i < rows.Count; i++) keys[i] ??= PollRules.EditKey(++issued);
        return (keys.Select(k => k!).ToArray(), issued);
    }

    private static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static string CleanDescription(string? s) =>
        string.Join("\n", Norm(s).Split('\n').Select(l => l.TrimEnd())).Trim('\n', ' ');
}
