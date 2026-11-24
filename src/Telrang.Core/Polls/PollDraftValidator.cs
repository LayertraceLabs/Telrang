namespace Telrang.Core.Polls;

/// <summary>
/// The single source of definition rules (DESIGN.md §20.1): validates a <see cref="PollDraft"/> into a
/// <see cref="PollDefinition"/>, or field-keyed errors. With <c>existing</c> it also applies the edit rules (DESIGN.md §26:
/// every setting fixed; an edit changes the title, description, options, close date, and (DESIGN.md §34.5) each question's
/// prompt, help, pages, groups and order, and adds and removes questions; question, page, group and option keys never come back).
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

        var draftQuestions = draft.Questions ?? [];
        var draftPages = draft.Pages ?? [];
        var draftGroups = draft.Groups ?? [];
        // A restored browser draft is untrusted: the counts are checked before any per-item work (AV-03).
        if (draftQuestions.Count == 0) Err("questions", "Add a question.");
        else if (draftQuestions.Count > limits.QuestionsMax) Err("questions", PollRules.TooManyQuestions(limits.QuestionsMax, draftQuestions.Count));
        if (draftPages.Count > limits.PagesMax) Err("pages", PollRules.TooManyPages(limits.PagesMax, draftPages.Count));
        if (draftGroups.Count > limits.GroupsMax) Err("groups", PollRules.TooManyGroups(limits.GroupsMax, draftGroups.Count));
        if (errors.Any(e => e.Field is "questions" or "pages" or "groups")) return new DraftResult(null, errors);

        // ---- pages and groups: the draft's own keys name them; stored keys are kept, new ones numbered from the counters ----
        var pages = ResolveLayout("pages", "page", 'p', draftPages.Select(p => (p?.Key, p?.Title, p?.Description)).ToList(),
            existing?.Pages.Select(p => p.Key) ?? [], existing?.PagesIssued ?? 0, limits, errors);
        var groups = ResolveLayout("groups", "group", 'g', draftGroups.Select(g => (g?.Key, g?.Title, g?.Description)).ToList(),
            existing?.Groups.Select(g => g.Key) ?? [], existing?.GroupsIssued ?? 0, limits, errors);
        var pageIndex = pages.Items.Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);

        // ---- question keys (DESIGN.md §34.2): a stored key a draft names is kept once, every other question is new ----
        var stored = existing?.Questions ?? [];
        var matched = new PollQuestion?[draftQuestions.Count];
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        for (var qi = 0; qi < draftQuestions.Count; qi++)
        {
            var wanted = draftQuestions[qi]?.Key;
            if (wanted is not null && stored.FirstOrDefault(s => s.Key == wanted) is { } hit && claimed.Add(hit.Key)) matched[qi] = hit;
        }
        // Drafts from before keys existed: the one question of a one-question poll.
        if (stored.Count == 1 && draftQuestions.Count == 1 && matched[0] is null && string.IsNullOrEmpty(draftQuestions[0]?.Key)) matched[0] = stored[0];
        var issuedQuestions = PollRules.IssuedSoFar(existing?.QuestionsIssued ?? 0, stored.Select(s => s.Key), 'q');

        var multi = draftQuestions.Count > 1;
        var questions = new List<PollQuestion>(draftQuestions.Count);
        var lastPage = -1;
        string? previousGroup = null;
        var closedGroups = new HashSet<string>(StringComparer.Ordinal);
        var groupPage = new Dictionary<string, string?>(StringComparer.Ordinal);
        var usedPages = new HashSet<string>(StringComparer.Ordinal);
        var usedGroups = new HashSet<string>(StringComparer.Ordinal);
        for (var qi = 0; qi < draftQuestions.Count; qi++)
        {
            var path = $"questions[{qi}]";
            var key = matched[qi]?.Key ?? PollRules.NumberedKey('q', ++issuedQuestions);

            var pageKey = Reference(draftQuestions[qi]?.Page);
            string? page = null;
            if (pageKey is not null)
            {
                if (pages.Keys.TryGetValue(pageKey, out var final)) page = final;
                else Err($"{path}.page", PollRules.NeedPageList);
            }
            else if (pages.Items.Count > 0) Err($"{path}.page", PollRules.NeedPageList);
            var groupKey = Reference(draftQuestions[qi]?.Group);
            string? group = null;
            if (groupKey is not null)
            {
                if (groups.Keys.TryGetValue(groupKey, out var final)) group = final;
                else Err($"{path}.group", PollRules.NeedGroupList);
            }

            // Questions in page order; a group's questions together on one page.
            if (page is not null)
            {
                usedPages.Add(page);
                var at = pageIndex[page];
                if (at < lastPage) Err($"{path}.page", PollRules.PageOrder);
                lastPage = Math.Max(lastPage, at);
            }
            if (group is not null)
            {
                usedGroups.Add(group);
                if (closedGroups.Contains(group)) Err($"{path}.group", PollRules.GroupTogether);
                else if (groupPage.TryGetValue(group, out var on) && on != page) Err($"{path}.group", PollRules.GroupTogether);
                groupPage.TryAdd(group, page);
            }
            if (previousGroup is not null && previousGroup != group) closedGroups.Add(previousGroup);
            previousGroup = group;

            var question = ValidateQuestion(draftQuestions[qi], path, limits, matched[qi], key, multi, page, group, errors);
            if (question is not null) questions.Add(question);
        }
        for (var i = 0; i < pages.Items.Count; i++)
            if (!usedPages.Contains(pages.Items[i].Key) && !errors.Any(e => e.Field == $"pages[{i}].title" || e.Field == $"pages[{i}].key"))
                Err($"pages[{i}]", PollRules.EmptyPage);
        for (var i = 0; i < groups.Items.Count; i++)
            if (!usedGroups.Contains(groups.Items[i].Key) && !errors.Any(e => e.Field == $"groups[{i}].title" || e.Field == $"groups[{i}].key"))
                Err($"groups[{i}]", PollRules.EmptyGroup);

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
            Pages = pages.Items.Count == 0 ? Array.Empty<PollPage>() : pages.Items.Select(p => new PollPage(p.Key, p.Title, p.Description)).ToList(),
            Groups = groups.Items.Count == 0 ? Array.Empty<PollGroup>() : groups.Items.Select(g => new PollGroup(g.Key, g.Title, g.Description)).ToList(),
            QuestionsIssued = issuedQuestions,
            PagesIssued = pages.Issued,
            GroupsIssued = groups.Issued,
        };
        return new DraftResult(def, errors);
    }

    /// <summary>A reference to a page or group in a draft: null when empty.</summary>
    private static string? Reference(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    private sealed record Layout(List<(string Key, string Title, string Description)> Items, Dictionary<string, string> Keys, int Issued);

    /// <summary>
    /// Pages or groups (DESIGN.md §34.2): checks each title and description, and gives each its final key. A draft key that
    /// names a stored one is kept (once); every other item takes the next number after everything ever issued
    /// (<see cref="PollRules.IssuedSoFar"/>), so a removed page or group's key never comes back. The returned dictionary maps
    /// the draft's own keys to the final ones.
    /// </summary>
    private static Layout ResolveLayout(string kind, string what, char prefix, IReadOnlyList<(string? Key, string? Title, string? Description)> drafts,
        IEnumerable<string> storedKeys, int storedCounter, PollLimits limits, List<DraftError> errors)
    {
        void Err(string field, string message) => errors.Add(new DraftError(field, message));
        var stored = storedKeys.ToHashSet(StringComparer.Ordinal);
        var issued = PollRules.IssuedSoFar(storedCounter, stored, prefix);
        var draftKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<(string Key, string Title, string Description)>(drafts.Count);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < drafts.Count; i++)
        {
            var title = (drafts[i].Title ?? "").Trim();
            if (title.Length == 0) Err($"{kind}[{i}].title", "Enter a title.");
            else if (title.Length > limits.TitleMax) Err($"{kind}[{i}].title", PollRules.TitleTooLongFor(what, limits, title.Length));
            else if (PollRules.HasHiddenCharacters(title)) Err($"{kind}[{i}].title", PollRules.HiddenCharactersIn("title"));
            var description = CleanDescription(drafts[i].Description);
            if (description.Length > limits.DescriptionMax) Err($"{kind}[{i}].description", PollRules.DescriptionTooLongFor(what, limits, description.Length));
            var draftKey = Reference(drafts[i].Key);
            if (draftKey is not null && !draftKeys.TryAdd(draftKey, i))
            {
                Err($"{kind}[{i}].key", $"Use a different key: another {what} has it.");
                draftKey = null;
            }
            string final;
            if (draftKey is not null && stored.Contains(draftKey) && kept.Add(draftKey)) final = draftKey;
            else final = PollRules.NumberedKey(prefix, ++issued);
            if (draftKey is not null) keys[draftKey] = final;
            items.Add((final, title, description));
        }
        return new Layout(items, keys, issued);
    }

    private static PollQuestion? ValidateQuestion(QuestionDraft q, string path, PollLimits limits, PollQuestion? existing, string key, bool multi,
        string? page, string? group, List<DraftError> errors)
    {
        var before = errors.Count;
        void Err(string field, string message) => errors.Add(new DraftError($"{path}.{field}", message));
        if (q is null) { errors.Add(new DraftError(path, "Add the question.")); return null; }

        var typeOk = System.Enum.IsDefined(q.Type);
        if (!typeOk) Err("type", "Choose a poll type.");
        var type = q.Type;

        // ---- text of the question ----
        var prompt = (q.Prompt ?? "").Trim();
        if (prompt.Length == 0 && multi) Err("prompt", PollRules.NeedPrompt);
        else if (prompt.Length > limits.PromptMax) Err("prompt", PollRules.PromptTooLong(limits, prompt.Length));
        else if (PollRules.HasHiddenCharacters(prompt)) Err("prompt", PollRules.HiddenCharactersIn("question"));
        var help = CleanDescription(q.Help);
        if (help.Length > limits.HelpMax) Err("help", PollRules.HelpTooLong(limits, help.Length));

        var scoring = ScoringScheme.Borda;
        var bans = 0;
        var rankAll = false;
        int? budget = null, maxPer = null;
        var spendAll = false;
        var maybe = true;
        int minSelect = 1, maxSelect = 1;
        var allowOther = false;
        var display = ChoiceDisplay.Buttons;
        var multiline = false;
        int? minLength = null, maxLength = null;
        decimal? min = null, max = null;
        var decimals = 0;
        var unit = "";

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
                case PollType.Choice:
                    allowOther = q.AllowOther;
                    if (System.Enum.IsDefined(q.Display)) display = q.Display;
                    else Err("display", "Choose buttons or a dropdown.");
                    if (q.MinSelect < 1) Err("minSelect", "Enter a minimum of 1 or more.");
                    else minSelect = q.MinSelect;
                    if (q.MaxSelect < 1) Err("maxSelect", "Enter a maximum of 1 or more.");
                    else maxSelect = q.MaxSelect;
                    if (q.MinSelect >= 1 && q.MaxSelect >= 1 && q.MinSelect > q.MaxSelect) Err("minSelect", "Lower the minimum to the maximum or less.");
                    if (q.MaxSelect >= 1 && display == ChoiceDisplay.Dropdown && maxSelect > 1) Err("display", PollRules.DropdownNeedsOne);
                    break;
                case PollType.Text:
                    multiline = q.Multiline;
                    if (q.MinLength is int ml)
                    {
                        // A stored length stays valid whatever the admin's limit became since.
                        if (ml < 0 || (ml > limits.TextAnswerMax && ml != existing?.MinLength)) Err("minLength", $"Enter a minimum between 0 and {limits.TextAnswerMax}.");
                        else if (ml > 0) minLength = ml;
                    }
                    // On edit an empty maximum keeps the stored one (the admin's limit may have changed since).
                    var wantedMax = q.MaxLength ?? (existing?.Type == PollType.Text ? existing.MaxLength : null);
                    if (wantedMax is int xl)
                    {
                        if (xl < 1 || (xl > limits.TextAnswerMax && xl != existing?.MaxLength)) Err("maxLength", $"Enter a maximum between 1 and {limits.TextAnswerMax}.");
                        else maxLength = xl;
                    }
                    else maxLength = limits.TextAnswerMax;
                    if (minLength is int lo && maxLength is int hi && lo > hi) Err("minLength", "Lower the minimum to the maximum or less.");
                    break;
                case PollType.Number:
                    if (q.Min is decimal mn && Math.Abs(mn) > PollRules.NumberAbsMax) Err("min", PollRules.NumberTooBig());
                    else min = q.Min;
                    if (q.Max is decimal mx && Math.Abs(mx) > PollRules.NumberAbsMax) Err("max", PollRules.NumberTooBig());
                    else max = q.Max;
                    if (min is decimal a && max is decimal z && a > z) Err("min", "Lower the minimum to the maximum or less.");
                    if (q.Decimals < 0 || q.Decimals > PollRules.MaxDecimals) Err("decimals", $"Enter 0 to {PollRules.MaxDecimals} decimal places.");
                    else
                    {
                        decimals = q.Decimals;
                        if (min is decimal m1 && PollRules.Scale(m1) > decimals) Err("min", PollRules.TooManyDecimals(decimals));
                        if (max is decimal m2 && PollRules.Scale(m2) > decimals) Err("max", PollRules.TooManyDecimals(decimals));
                    }
                    unit = (q.Unit ?? "").Trim();
                    if (unit.Length > PollRules.UnitMax) Err("unit", $"Shorten the unit to {PollRules.UnitMax} characters (now {unit.Length}).");
                    else if (PollRules.HasHiddenCharacters(unit)) Err("unit", PollRules.HiddenCharactersIn("unit"));
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        // ---- options ----
        var rows = new List<Row>();
        var inputs = PollRules.UsesOptions(type) ? q.Options ?? [] : [];
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
        if (PollRules.UsesOptions(type) && inputs.Count < 2) Err("options", PollRules.TooFewOptions(inputs.Count));
        if (type == PollType.Ranked && typeOk && bans > 0 && inputs.Count >= 2 && bans >= inputs.Count)
        {
            // On edit the bans are fixed: the options are what has to change.
            if (existing is not null) Err("options", PollRules.KeepMoreOptionsThanBans(bans));
            else Err("bans", PollRules.BansTooMany(bans, inputs.Count));
        }
        if (type == PollType.Choice && typeOk && inputs.Count >= 2 && q.MaxSelect >= 1 && maxSelect > inputs.Count + (allowOther ? 1 : 0))
        {
            // On edit the maximum is fixed: the options are what has to change.
            if (existing is not null) Err("options", PollRules.TooFewForMax(maxSelect - (allowOther ? 1 : 0)));
            else Err("maxSelect", PollRules.MaxSelectTooBig(inputs.Count + (allowOther ? 1 : 0)));
        }

        // Edit rules (DESIGN.md §26, §34.5): the type and its rules are fixed at creation. A question the draft names by key
        // is the stored one; every other question is new and free.
        if (existing is not null && typeOk)
        {
            if (existing.Type != type) Err("type", "The poll type cannot be changed after creation.");
            else
            {
                if (existing.Required != q.Required) Err("required", FixedMessage("Required"));
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
                    case PollType.Choice:
                        if (existing.MinSelect != q.MinSelect) Err("minSelect", FixedMessage("The minimum selections"));
                        if (existing.MaxSelect != q.MaxSelect) Err("maxSelect", FixedMessage("The maximum selections"));
                        if (existing.AllowOther != allowOther) Err("allowOther", FixedMessage("Other"));
                        if (existing.Display != display) Err("display", FixedMessage("The display"));
                        break;
                    case PollType.Text:
                        if (existing.Multiline != multiline) Err("multiline", FixedMessage("Multiple lines"));
                        if ((existing.MinLength ?? 0) != (q.MinLength ?? 0)) Err("minLength", FixedMessage("The minimum length"));
                        if (existing.MaxLength != maxLength) Err("maxLength", FixedMessage("The maximum length"));
                        break;
                    case PollType.Number:
                        if (existing.Min != q.Min) Err("min", FixedMessage("The minimum"));
                        if (existing.Max != q.Max) Err("max", FixedMessage("The maximum"));
                        if (existing.Decimals != q.Decimals) Err("decimals", FixedMessage("The decimal places"));
                        if (!string.Equals(existing.Unit, unit, StringComparison.Ordinal)) Err("unit", FixedMessage("The unit"));
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
            Prompt = prompt,
            HelpMarkdown = help,
            Required = q.Required,
            Page = page,
            Group = group,
            Scoring = scoring,
            MaxBans = bans,
            RankAll = rankAll,
            Budget = budget,
            MaxPerOption = maxPer,
            SpendAll = spendAll,
            Maybe = maybe,
            MinSelect = minSelect,
            MaxSelect = maxSelect,
            AllowOther = allowOther,
            Display = display,
            Multiline = multiline,
            MinLength = minLength,
            MaxLength = maxLength,
            Min = min,
            Max = max,
            Decimals = decimals,
            Unit = unit,
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
