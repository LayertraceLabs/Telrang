using System.Globalization;
using System.Text.RegularExpressions;
using Telrang.Core.Polls;
using Telrang.Web.Features.Voting;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// The poll form (DESIGN.md §20.2), used by the new and edit pages. Works on a <see cref="PollDraft"/>, validates it on
/// every change with <see cref="PollParsing.Validate"/> (the same call the server makes on save) and shows each
/// <see cref="DraftError"/> under its control. With a <see cref="DraftKey"/> the draft is kept in the browser
/// (wwwroot/js/draft.js) until saved.
/// </summary>
public sealed partial class PollEditor : IAsyncDisposable
{
    [Inject] private PollService Polls { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Time { get; set; } = default!;

    /// <summary>The starting state (blank, a copy, or the stored poll). The editor works on its own copy.</summary>
    [Parameter, EditorRequired] public PollDraft Initial { get; set; } = default!;

    /// <summary>On edit: the stored definition (edit rules: type and scoring are fixed).</summary>
    [Parameter] public PollDefinition? Existing { get; set; }

    /// <summary>Browser draft key from <see cref="DraftKeyFor"/>; null = no draft.</summary>
    [Parameter] public string? DraftKey { get; set; }

    /// <summary>A copy of another poll was opened: it replaces a pending browser draft.</summary>
    [Parameter] public bool ReplaceDraft { get; set; }

    /// <summary>The zone used while the time zone field is empty (account zone on create, stored zone on edit).</summary>
    [Parameter] public string? DefaultTimeZone { get; set; }

    /// <summary>
    /// New poll, account without a time zone (DESIGN.md §23.5): after the first render the field takes the browser's zone
    /// if it is a known IANA id and the field still holds <see cref="DefaultTimeZone"/>. A restored browser draft keeps its own.
    /// </summary>
    [Parameter] public bool BrowserTimeZone { get; set; }

    /// <summary>Max poll duration in days (0 = none), counted from <see cref="CreatedAt"/> (now when null).</summary>
    [Parameter] public int MaxDurationDays { get; set; }
    [Parameter] public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>On edit: the stored close instant, shown while the close date is empty and was empty before.</summary>
    [Parameter] public DateTimeOffset? StoredClosesAt { get; set; }

    /// <summary>Raised after each change with the validation result (definition or errors).</summary>
    [Parameter] public EventCallback<DraftResult> Validated { get; set; }

    /// <summary>The page's buttons and messages, rendered under the form.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// The browser draft key: <c>draft:&lt;userId&gt;:&lt;pollId|new&gt;</c>. Per account, so a draft made by one account
    /// is never offered to another on the same browser; null (no draft) without a user.
    /// </summary>
    public static string? DraftKeyFor(string? userId, string? encodedPollId) =>
        userId is null ? null : $"draft:{userId}:{encodedPollId ?? "new"}";

    private const string Q = "questions[0]";
    private static int counter;
    private readonly string uid = "pe" + Interlocked.Increment(ref counter).ToString(CultureInfo.InvariantCulture);

    private PollDraft draft = new();
    private DraftResult result = new(null, []);
    private PollDraft? baseline;
    private bool revealAll;
    private readonly HashSet<string> touched = new(StringComparer.Ordinal);
    private readonly HashSet<string> claimed = new(StringComparer.Ordinal);

    // Options: List/Calendar or Text, and the other type family's options while it is not selected.
    private bool textMode;
    private string optionsText = "";
    private IReadOnlyList<OptionListError> textErrors = [];
    private int moreTextErrors;
    private List<OptionDraft>? stashText, stashDates;
    // OptionDraft is a record (value equality): two blank options are "equal", so every per-option lookup and @key
    // goes by reference.
    private readonly HashSet<OptionDraft> detailsOpen = new(ReferenceEqualityComparer.Instance);
    private OptionDraft? focusOption;
    private readonly Dictionary<OptionDraft, ElementReference> labelRefs = new(ReferenceEqualityComparer.Instance);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<OptionDraft, object> keys = new();

    /// <summary>A stable render key per option object.</summary>
    private object KeyOf(OptionDraft o) => keys.GetValue(o, _ => new object());

    private bool descriptionPreview;

    /// <summary>Bumped when a switch is refused: the radios are re-created so the browser's checked state matches again.</summary>
    private int segNonce;

    // Dates calendar (DESIGN.md §27): the month shown, the page of the per-day settings, a refused selection, and the
    // drag selection module (js/date-select.js) attached to the calendar element it was last given.
    private DateOnly month;
    private int dayPage;
    private string? calendarError;
    private readonly Dictionary<DateOnly, string> slotInput = [];
    private readonly Dictionary<DateOnly, string> slotError = [];
    private ElementReference calendarRoot;
    private bool calendarShown;
    private string? calendarAttached;
    private IJSObjectReference? selectModule;
    private DotNetObjectReference<PollEditor>? selfRef;

    private static readonly string[] WeekdayLong = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    /// <summary>Fold-all state of the editor's option help (DESIGN.md §23.4).</summary>
    private readonly Telrang.Web.Components.Layout.HelpState help = new(Telrang.Web.Components.Layout.HelpState.Editor);

    private IJSObjectReference? draftModule;
    private bool restored;
    private string? lastJson;

    private QuestionDraft Question => draft.Questions[0];
    private PollType Type => Question.Type;

    /// <summary>The draft as the form shows it now.</summary>
    public PollDraft Current => draft;

    /// <summary>The form can be saved: no field errors and no unparsed option text.</summary>
    public bool IsValid => result.Success && textErrors.Count == 0;

    protected override void OnInitialized() => Load(Copy(Initial), asBaseline: true);

    /// <summary>
    /// Keeps the results settings consistent with the rest (DESIGN.md §25), so no choice leaves the form in an error a
    /// disabled control cannot fix: results for anyone only on public polls, after closing only with an audience, ballots
    /// only with names.
    /// </summary>
    public static void Normalise(PollDraft d)
    {
        if (d.VoterMode != VoterMode.Public && d.ResultsVisibility == ResultsVisibility.Open) d.ResultsVisibility = ResultsVisibility.AfterVote;
        if (d.ResultsVisibility == ResultsVisibility.Creator) d.ResultsTiming = ResultsTiming.WhileOpen;
        if (d.Names == NamesMode.None) d.ShowBallots = false;
    }

    /// <summary>
    /// On edit (DESIGN.md §26) the fixed settings are the stored ones, also in a restored browser draft: the form shows them
    /// read-only, so nothing the editor sends can change them (the validator refuses it anyway).
    /// </summary>
    public static void PinFixed(PollDraft d, PollDefinition existing)
    {
        var fresh = PollDraft.FromDefinition(existing);
        d.VoterMode = fresh.VoterMode;
        d.Responses = fresh.Responses;
        d.Names = fresh.Names;
        d.ResultsVisibility = fresh.ResultsVisibility;
        d.ResultsTiming = fresh.ResultsTiming;
        d.ShowBallots = fresh.ShowBallots;
        d.TimeZone = fresh.TimeZone;
        if (d.Questions.Count == 0 || fresh.Questions.Count == 0) return;
        var (q, f) = (d.Questions[0], fresh.Questions[0]);
        (q.Type, q.Scoring, q.Bans, q.RankAll) = (f.Type, f.Scoring, f.Bans, f.RankAll);
        (q.Budget, q.MaxPerOption, q.SpendAll, q.Maybe) = (f.Budget, f.MaxPerOption, f.SpendAll, f.Maybe);
    }

    private void Load(PollDraft d, bool asBaseline)
    {
        if (d.Questions.Count == 0) d.Questions.Add(new QuestionDraft());
        if (Existing is null) Normalise(d);
        else PinFixed(d, Existing);
        if (Existing is null && string.IsNullOrWhiteSpace(d.TimeZone)) d.TimeZone = DefaultTimeZone;
        draft = d;
        if (asBaseline) baseline = Copy(d);
        textMode = false;
        textErrors = [];
        stashText = stashDates = null;
        detailsOpen.Clear();
        foreach (var o in Question.Options.Where(o => !string.IsNullOrWhiteSpace(o.Description))) detailsOpen.Add(o);
        if (Type != PollType.Dates && Question.Options.Count == 0)
            Question.Options.AddRange([new OptionDraft(), new OptionDraft()]);
        if (Type == PollType.Dates) SortDates();
        dayPage = 0;
        calendarError = null;
        month = FirstOfMonth(Question.Options.Where(o => o.Date is not null).Select(o => o.Date!.Value).DefaultIfEmpty(Today).Min());
        revealAll = Existing is not null;
        touched.Clear();
        Validate();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        foreach (var stale in labelRefs.Keys.Where(k => !Question.Options.Any(o => ReferenceEquals(o, k))).ToList()) labelRefs.Remove(stale);
        if (focusOption is { } f && labelRefs.TryGetValue(f, out var el))
        {
            focusOption = null;
            try { await el.FocusAsync(); } catch (JSException) { } catch (JSDisconnectedException) { }
        }
        if (RendererInfo.IsInteractive) await AttachDateSelectAsync();
        if (!firstRender || !RendererInfo.IsInteractive) return;
        // Before the draft is attached: the browser's zone is part of the starting state (no unsaved change), and a
        // restored draft then replaces it with its own.
        if (BrowserTimeZone && Existing is null && await ApplyBrowserTimeZoneAsync())
        {
            await Validated.InvokeAsync(result);
            StateHasChanged();
        }
        if (DraftKey is null) return;
        try
        {
            lastJson = PollJson.Serialize(draft);
            draftModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/draft.js");
            var stored = await draftModule.InvokeAsync<string?>("attach", DraftKey, lastJson, ReplaceDraft);
            if (stored is null) return;
            if (PollJson.DeserializeDraft(stored) is { Questions.Count: 1 } restoredDraft
                && (Existing is null || restoredDraft.Questions[0].Type == Existing.Questions[0].Type)
                && DraftFits(restoredDraft, Polls.CurrentLimits))
            {
                Load(restoredDraft, asBaseline: false);
                restored = true;
                lastJson = stored;
                await draftModule.InvokeVoidAsync("update", DraftKey, stored);
                await Validated.InvokeAsync(result);
                StateHasChanged();
            }
            else
            {
                await draftModule.InvokeVoidAsync("markSaved", DraftKey, lastJson); // an unreadable (old) or oversized draft: dropped
            }
        }
        catch (JSDisconnectedException) { }
        catch (JSException)
        {
            // draft.js failed to load or run (DESIGN.md §23.1): the editor works on without a browser draft.
            draftModule = null;
        }
    }

    /// <summary>
    /// The zone the time zone field takes from the browser (DESIGN.md §23.5): <paramref name="browserZone"/> when it is a known
    /// IANA id and the field still holds the server default; else null (the field stays).
    /// </summary>
    public static string? BrowserZoneToApply(string? browserZone, string? field, string? serverDefault) =>
        PollTime.IsKnown(browserZone) && browserZone != field && string.Equals(field, serverDefault, StringComparison.Ordinal)
            ? browserZone
            : null;

    /// <summary>Sets the browser's time zone (DESIGN.md §23.5) on the form and its baseline. True when it changed the form.</summary>
    private async Task<bool> ApplyBrowserTimeZoneAsync()
    {
        string? zone;
        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/timezone.js");
            zone = await module.InvokeAsync<string?>("browserZone");
        }
        catch (JSDisconnectedException) { return false; }
        catch (JSException) { return false; } // the server default stays
        if (BrowserZoneToApply(zone, draft.TimeZone, DefaultTimeZone) is not { } apply) return false;
        draft.TimeZone = apply;
        if (baseline is not null) baseline.TimeZone = apply;
        Validate();
        return true;
    }

    /// <summary>
    /// A restored browser draft is used only within the limits (R2-POLLS-2): at most <see cref="PollLimits.OptionsMax"/>
    /// options and no text over twice its limit (a draft may hold text being shortened). Anything bigger was not made by
    /// this form; rendering it would cost the server per option and per character.
    /// </summary>
    public static bool DraftFits(PollDraft d, PollLimits limits)
    {
        static bool Within(string? s, int max) => (s?.Length ?? 0) <= 2 * max;
        if (!Within(d.Title, limits.TitleMax) || !Within(d.Description, limits.DescriptionMax) || (d.TimeZone?.Length ?? 0) > 100) return false;
        return d.Questions.All(q => q is not null && q.Options is not null && q.Options.Count <= limits.OptionsMax
                                    && q.Options.All(o => o is not null && Within(o.Label, limits.OptionLabelMax)
                                                          && Within(o.Description, limits.OptionDescriptionMax)));
    }

    /// <summary>Call after a successful save: the current form becomes the baseline and the browser draft is dropped.</summary>
    public async Task MarkSavedAsync(PollDraft? saved = null)
    {
        restored = false;
        if (saved is not null) Load(Copy(saved), asBaseline: true);
        else baseline = Copy(draft);
        lastJson = PollJson.Serialize(draft);
        if (draftModule is null || DraftKey is null) return;
        try { await draftModule.InvokeVoidAsync("markSaved", DraftKey, lastJson); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    /// <summary>Shows every field error (after a save attempt). True when the form is valid.</summary>
    public bool RevealErrors()
    {
        revealAll = true;
        StateHasChanged();
        return IsValid;
    }

    private async Task DiscardDraftAsync()
    {
        Load(Copy(baseline ?? new PollDraft()), asBaseline: true);
        await MarkSavedAsync();
        await Validated.InvokeAsync(result);
    }

    // ---- change handling ----

    private void Validate() =>
        result = PollParsing.Validate(draft, Polls.CurrentLimits, MaxDurationDays, CreatedAt ?? Time.GetUtcNow(), Existing);

    /// <summary>After every edit: cut oversized text, mark the field touched, revalidate, tell the page, keep the browser draft.</summary>
    private async Task ChangedAsync(string field)
    {
        ClampTexts(draft, Polls.CurrentLimits);
        touched.Add(field);
        Validate();
        await Validated.InvokeAsync(result);
        await SaveDraftAsync();
    }

    private Task Changed(string field) => ChangedAsync(field);

    /// <summary>How far past its limit a text may be kept while the author shortens it: the margin <see cref="DraftFits"/> allows.</summary>
    public const int TextMargin = 2;

    /// <summary>Longest text kept in the slot field of a day and in the time zone field.</summary>
    public const int ShortTextMax = 100;

    /// <summary>
    /// Cuts every text of the draft at <see cref="TextMargin"/> times its limit (DESIGN.md §31, pentest R3-RES-2): the
    /// browser's <c>maxlength</c> is no bound for a crafted event, and the circuit keeps the draft and serialises it on
    /// every change. Validation still reports the real limit ("Shorten …"); a draft never exceeds what a restored draft may
    /// hold.
    /// </summary>
    public static void ClampTexts(PollDraft d, PollLimits limits)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(limits);
        d.Title = Cut(d.Title, limits.TitleMax);
        d.Description = Cut(d.Description, limits.DescriptionMax);
        if (d.TimeZone is { Length: > ShortTextMax } zone) d.TimeZone = zone[..ShortTextMax];
        foreach (var q in d.Questions)
        {
            if (q?.Options is null) continue;
            foreach (var o in q.Options)
            {
                if (o is null) continue;
                o.Label = Cut(o.Label, limits.OptionLabelMax);
                o.Description = Cut(o.Description, limits.OptionDescriptionMax);
            }
        }
    }

    /// <summary><paramref name="text"/> cut at <see cref="TextMargin"/> times <paramref name="limit"/> (at least 1).</summary>
    public static string Cut(string? text, int limit)
    {
        var max = TextMargin * Math.Max(1, limit);
        return text is null ? "" : text.Length <= max ? text : text[..max];
    }

    private async Task SaveDraftAsync()
    {
        if (draftModule is null || DraftKey is null) return;
        var json = PollJson.Serialize(draft);
        if (json == lastJson) return;
        // Never push more than a circuit message may carry (the JSON escapes every non-ASCII character, so its length is
        // its size in bytes): the clamp above keeps a real draft below it.
        if (json.Length > Settings.LimitSettings.MaxCircuitMessageBytes) return;
        lastJson = json;
        try { await draftModule.InvokeVoidAsync("update", DraftKey, json); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    private async Task SetTypeAsync(PollType type)
    {
        if (Existing is not null || type == Type) return;
        var wasDates = Type == PollType.Dates;
        var isDates = type == PollType.Dates;
        if (wasDates != isDates)
        {
            // The other family's options wait in a stash: switching back restores them.
            if (wasDates) { stashDates = Question.Options; Question.Options = stashText ?? [new OptionDraft(), new OptionDraft()]; }
            else { stashText = Question.Options; Question.Options = stashDates ?? []; }
            textMode = false;
            textErrors = [];
        }
        Question.Type = type;
        if (type == PollType.Points && Question.Budget is null) Question.Budget = 100;
        if (textMode) ParseText();
        await ChangedAsync($"{Q}.type");
    }

    // ---- errors ----

    private bool Visible(DraftError e) =>
        revealAll || touched.Any(t => e.Field == t || e.Field.StartsWith(t + ".", StringComparison.Ordinal)
                                      || e.Field.StartsWith(t + "[", StringComparison.Ordinal));

    /// <summary>The visible messages of one field; the field counts as placed (not repeated in the summary).</summary>
    private List<string> ErrorsOf(string field)
    {
        claimed.Add(field);
        return result.Errors.Where(e => e.Field == field && Visible(e)).Select(e => e.Message).ToList();
    }

    private bool HasError(string field) => result.Errors.Any(e => e.Field == field && Visible(e));

    private static string Fid(string field) => "f-" + NonId().Replace(field, "-").Trim('-');
    private string Id(string field) => uid + "-" + Fid(field);
    private string ErrId(string field) => Id(field) + "-err";
    private string? DescribedBy(string field, string? extra = null)
    {
        var ids = new List<string>();
        if (extra is not null) ids.Add(extra);
        if (HasError(field)) ids.Add(ErrId(field));
        return ids.Count == 0 ? null : string.Join(' ', ids);
    }
    private string Invalid(string field) => HasError(field) ? "is-invalid" : "";
    private string? AriaInvalid(string field) => HasError(field) ? "true" : null;

    /// <summary>Visible errors no control showed (e.g. "questions"), listed above the buttons.</summary>
    private IEnumerable<DraftError> Unplaced => result.Errors.Where(e => Visible(e) && !claimed.Contains(e.Field));

    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex NonId();

    // ---- options: list ----

    private string OptionField(int i, string f) => $"{Q}.options[{i}].{f}";

    private int OptionsMax => Polls.CurrentLimits.OptionsMax;

    /// <summary>The options limit of a dates question: the admin's, at most <see cref="Telrang.Core.Polls.PollRules.MaxDateOptions"/> (DESIGN.md §27).</summary>
    private int DatesMax => Telrang.Core.Polls.PollRules.OptionsLimit(PollType.Dates, Polls.CurrentLimits);

    /// <summary>No add (option, day, time) may take the list past the limit (R2-POLLS-2).</summary>
    private bool AtMax => Question.Options.Count >= Telrang.Core.Polls.PollRules.OptionsLimit(Type, Polls.CurrentLimits);

    private Task AddOption()
    {
        if (AtMax) return Task.CompletedTask;
        var o = new OptionDraft();
        Question.Options.Add(o);
        focusOption = o;
        return ChangedAsync($"{Q}.options");
    }

    private Task MoveAsync(int i, int delta)
    {
        var list = Question.Options;
        var j = i + delta;
        if (j < 0 || j >= list.Count) return Task.CompletedTask;
        (list[i], list[j]) = (list[j], list[i]);
        return ChangedAsync($"{Q}.options");
    }

    private Task RemoveAsync(int i)
    {
        Question.Options.RemoveAt(i);
        return ChangedAsync($"{Q}.options");
    }

    // ---- options: text ----

    private async Task SetTextModeAsync(bool text)
    {
        if (text == textMode) return;
        if (text)
        {
            if (Type == PollType.Dates) SortDates();
            optionsText = OptionListParser.Format(Question.Options.Where(o => Type == PollType.Dates ? o.Date is not null : true), Type);
            keysBeforeText = Question.Options.Where(o => o.Key is not null).Select(o => (LabelOf(o, Type), o.Key!)).ToList();
            textErrors = [];
            moreTextErrors = 0;
            textMode = true;
        }
        else if (!TryLeaveText()) { segNonce++; return; }
        await ChangedAsync($"{Q}.options");
    }

    /// <summary>Text → list: only when the text parses; else the line errors stay in view.</summary>
    private bool TryLeaveText()
    {
        ParseText();
        if (textErrors.Count > 0) { touched.Add($"{Q}.optionsText"); return false; }
        textMode = false;
        foreach (var o in Question.Options.Where(o => !string.IsNullOrWhiteSpace(o.Description))) detailsOpen.Add(o);
        if (Type == PollType.Dates) month = FirstOfMonth(Question.Options.Select(o => o.Date ?? Today).DefaultIfEmpty(Today).Min());
        return true;
    }

    private void ParseText()
    {
        var parsed = OptionListParser.Parse(optionsText, Type, Polls.CurrentLimits);
        textErrors = parsed.Errors;
        moreTextErrors = parsed.MoreErrors;
        if (!parsed.Success) return;
        Question.Options = parsed.Options.ToList();
        CarryKeys(Question.Options, Question.Options.Count == 0 ? [] : keysBeforeText, Type);
    }

    /// <summary>The option keys of the list when Text mode opened (label → key).</summary>
    private List<(string Label, string Key)> keysBeforeText = [];

    /// <summary>
    /// Text mode keeps option keys by exact label (DESIGN.md §26): a line whose label (for dates the normalised date label)
    /// is the label an option had when Text mode opened keeps that option's key, so a label changed in the list and then
    /// listed as text is still the same option. Each key goes to one line. Lines without one get theirs from the validator
    /// (a stored option with that exact label, else a new key).
    /// </summary>
    public static void CarryKeys(List<OptionDraft> parsed, IReadOnlyList<(string Label, string Key)> before, PollType type)
    {
        var free = before.ToList();
        foreach (var o in parsed)
        {
            var label = LabelOf(o, type);
            var i = free.FindIndex(b => string.Equals(b.Label, label, StringComparison.Ordinal));
            if (i < 0) continue;
            o.Key = free[i].Key;
            free.RemoveAt(i);
        }
    }

    private static string LabelOf(OptionDraft o, PollType type) =>
        type == PollType.Dates && o.Date is DateOnly d ? Telrang.Core.Polls.PollRules.DateLabel(d, o.Start, o.End) : (o.Label ?? "").Trim();

    /// <summary>
    /// The text is parsed when the textarea commits (change: focus leaves it), on a mode switch and before a save, not
    /// on every input event: one keystroke never sends and re-parses the whole list (AV-03).
    /// </summary>
    private Task OnTextChangedAsync()
    {
        var max = TextMargin * Math.Max(1, OptionListParser.MaxTextLength(Polls.CurrentLimits));
        if (optionsText.Length > max) optionsText = optionsText[..max];
        ParseText();
        return ChangedAsync($"{Q}.optionsText");
    }

    // ---- options: dates calendar ----

    /// <summary>
    /// Attaches js/date-select.js (drag, touch hold and Shift+arrow ranges) to the calendar element once per element: the
    /// calendar is re-created when the type or the Calendar / Text mode changes. Without the module clicks still toggle days.
    /// </summary>
    private async Task AttachDateSelectAsync()
    {
        if (!calendarShown || calendarRoot.Id is not { Length: > 0 } id || id == calendarAttached) return;
        calendarAttached = id;
        try
        {
            selectModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/date-select.js");
            selfRef ??= DotNetObjectReference.Create(this);
            await selectModule.InvokeVoidAsync("attach", calendarRoot, selfRef);
        }
        catch (JSDisconnectedException) { }
        catch (JSException)
        {
            selectModule = null; // DESIGN.md §23.1: single clicks keep working
        }
    }

    /// <summary>
    /// A finished range from js/date-select.js: every day from <paramref name="from"/> to <paramref name="to"/> gets the
    /// state <paramref name="select"/>. Only days of the month shown are accepted (the browser sends what it shows; a forged
    /// call cannot make the server walk a long range).
    /// </summary>
    [JSInvokable]
    public async Task SelectDays(string from, string to, bool select)
    {
        if (textMode || Type != PollType.Dates || !TryDay(from, out var a) || !TryDay(to, out var b)) return;
        if (FirstOfMonth(a) != month || FirstOfMonth(b) != month) return;
        await SetDaysAsync(DateEditing.Range(a, b), select);
        StateHasChanged();
    }

    private static bool TryDay(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    /// <summary>Selects or deselects days; a selection past the limit is refused whole, saying how many fit.</summary>
    private Task SetDaysAsync(IReadOnlyList<DateOnly> days, bool select)
    {
        if (DateEditing.SetDays(Question.Options, days, select, DatesMax) is { } refused)
        {
            calendarError = refused;
            return Task.CompletedTask;
        }
        calendarError = null;
        foreach (var d in days) slotError.Remove(d);
        if (select && days.Count > 0)
        {
            var page = DateEditing.PageOf(Days.Select(g => g.Key).ToList(), days.Min());
            if (page >= 0) dayPage = page;
        }
        return ChangedAsync($"{Q}.options");
    }

    private Task ToggleWeekdayAsync(int weekday)
    {
        var days = DateEditing.Weekdays(month.Year, month.Month, weekday);
        return SetDaysAsync(days, DateEditing.HeaderSelects(Question.Options, days));
    }

    /// <summary>Pages (0-based) of the per-day settings that hold a day with a visible field error.</summary>
    private HashSet<int> ErrorPages(List<IGrouping<DateOnly, OptionDraft>> days)
    {
        var pages = new HashSet<int>();
        var order = days.Select(g => g.Key).ToList();
        for (var i = 0; i < Question.Options.Count; i++)
        {
            if (Question.Options[i].Date is not { } d) continue;
            var prefix = $"{Q}.options[{i}].";
            if (result.Errors.Any(e => e.Field.StartsWith(prefix, StringComparison.Ordinal) && Visible(e)) || slotError.ContainsKey(d))
            {
                var page = DateEditing.PageOf(order, d);
                if (page >= 0) pages.Add(page);
            }
        }
        return pages;
    }

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Time.GetUtcNow(), PollTime.Zone(draft.TimeZone ?? DefaultTimeZone)).DateTime);

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private void SortDates() =>
        Question.Options = Question.Options.OrderBy(o => o.Date).ThenBy(o => o.Start).ThenBy(o => o.End).ToList();

    private IEnumerable<IGrouping<DateOnly, OptionDraft>> Days =>
        Question.Options.Where(o => o.Date is not null).GroupBy(o => o.Date!.Value).OrderBy(g => g.Key);

    private bool IsSelected(DateOnly d) => Question.Options.Any(o => o.Date == d);

    private Task ToggleDayAsync(DateOnly d) => SetDaysAsync([d], !IsSelected(d));

    private Task RemoveDayAsync(DateOnly d) => SetDaysAsync([d], false);

    /// <summary>Parses "19:00", "18:30-21:00" or "18:30–21:00" with the Text mode's rules (one date line).</summary>
    private static bool TryParseSlot(DateOnly day, string text, out TimeOnly? start, out TimeOnly? end, out string? error)
    {
        start = end = null;
        var line = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + text.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (!Telrang.Core.Polls.PollRules.TryParseDateItem(line, 1, out var slots, out error) || slots.Count != 1 || slots[0].Start is null)
        {
            if (error is null || error == Telrang.Core.Polls.PollRules.DateItemHelp) error = "Write a time like 19:00 or 18:30–21:00.";
            return false;
        }
        (start, end) = (slots[0].Start, slots[0].End);
        return true;
    }

    private void SetSlotInput(DateOnly day, object? value)
    {
        var text = value?.ToString() ?? "";
        slotInput[day] = text.Length <= ShortTextMax ? text : text[..ShortTextMax];
    }

    private Task AddSlotAsync(DateOnly day)
    {
        var text = slotInput.GetValueOrDefault(day) ?? "";
        if (!TryParseSlot(day, text, out var start, out var end, out var error))
        {
            slotError[day] = error!;
            return Task.CompletedTask;
        }
        return AddSlotCoreAsync(day, start, end, clearInput: true);
    }

    /// <summary>A time from the clock picker (DESIGN.md §27): added like a typed one; the typed text stays as it was.</summary>
    private Task AddPickedSlotAsync(DateOnly day, TimeOnly start, TimeOnly? end) => AddSlotCoreAsync(day, start, end, clearInput: false);

    private Task AddSlotCoreAsync(DateOnly day, TimeOnly? start, TimeOnly? end, bool clearInput)
    {
        var options = Question.Options.Where(o => o.Date == day).ToList();
        if (options.Any(o => o.Start == start && o.End == end))
        {
            slotError[day] = "This time is already listed.";
            return Task.CompletedTask;
        }
        if (Question.Options.Count >= DatesMax && options.Any(o => o.Start is not null || o.End is not null))
        {
            slotError[day] = DateEditing.SlotRefused(DatesMax);
            return Task.CompletedTask;
        }
        var description = options.Select(o => o.Description).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
        // An all-day day turns into a day with slots.
        Question.Options.RemoveAll(o => o.Date == day && o.Start is null && o.End is null);
        Question.Options.Add(new OptionDraft { Date = day, Start = start, End = end, Description = description });
        if (clearInput) slotInput[day] = "";
        slotError.Remove(day);
        SortDates();
        return ChangedAsync($"{Q}.options");
    }

    private async Task SlotKeyAsync(DateOnly day, Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await AddSlotAsync(day);
    }

    private Task RemoveSlotAsync(OptionDraft slot)
    {
        var day = slot.Date!.Value;
        Question.Options.RemoveAll(o => ReferenceEquals(o, slot));
        if (!Question.Options.Any(o => o.Date == day))
            Question.Options.Add(new OptionDraft { Date = day, Description = slot.Description }); // back to all day
        SortDates();
        return ChangedAsync($"{Q}.options");
    }

    /// <summary>Every other selected day gets this day's time slots (its own details stay); refused past the limit.</summary>
    private Task ApplyToAllAsync(DateOnly day)
    {
        if (DateEditing.ApplySlots(Question.Options, day, DatesMax) is { } refused)
        {
            slotError[day] = refused;
            return Task.CompletedTask;
        }
        slotError.Clear();
        return ChangedAsync($"{Q}.options");
    }

    private static string DayDescription(IEnumerable<OptionDraft> options) =>
        options.Select(o => o.Description).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";

    private Task SetDayDescriptionAsync(DateOnly day, string? text)
    {
        foreach (var o in Question.Options.Where(o => o.Date == day)) o.Description = text ?? "";
        return ChangedAsync($"{Q}.options");
    }

    /// <summary>Errors of the options on one day (validator paths by option index).</summary>
    private List<string> DayErrors(DateOnly day)
    {
        var list = new List<string>();
        for (var i = 0; i < Question.Options.Count; i++)
        {
            if (Question.Options[i].Date != day) continue;
            foreach (var f in new[] { "date", "start", "end", "description" }) list.AddRange(ErrorsOf(OptionField(i, f)));
        }
        return list;
    }

    private static string SlotText(OptionDraft o) => o.Start is { } s
        ? s.ToString("HH:mm", CultureInfo.InvariantCulture) + (o.End is { } e ? "–" + e.ToString("HH:mm", CultureInfo.InvariantCulture) : "")
        : "All day";

    // ---- closing ----

    private DateOnly? LatestClose => PollParsing.LatestClose(MaxDurationDays, CreatedAt ?? Time.GetUtcNow());

    /// <summary>What an empty close date means: the stored automatic close on edit, else creation + the max duration.</summary>
    private string AutoCloseText
    {
        get
        {
            var zone = draft.TimeZone is { Length: > 0 } z && PollTime.IsKnown(z) ? z : DefaultTimeZone ?? PollTime.Utc;
            if (Existing is { ClosesOn: null } && StoredClosesAt is { } stored) return "Empty: closes automatically on " + PollTime.Format(stored, zone) + ".";
            if (Existing is not null && CreatedAt is { } created && PollState.AutoCloseAt(created, MaxDurationDays) is { } at)
                return "Empty: closes automatically on " + PollTime.Format(at, zone) + ".";
            return MaxDurationDays > 0
                ? $"Empty: closes automatically after {MaxDurationDays} {(MaxDurationDays == 1 ? "day" : "days")}."
                : "Empty: stays open until you close it.";
        }
    }

    // ---- helpers ----

    private static PollDraft Copy(PollDraft d) => PollJson.DeserializeDraft(PollJson.Serialize(d)) ?? new PollDraft();

    private string Name(string group) => uid + "-" + group;

    public async ValueTask DisposeAsync()
    {
        if (selectModule is not null)
        {
            try { await selectModule.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
        }
        selfRef?.Dispose();
        if (draftModule is null) return;
        try
        {
            if (DraftKey is not null) await draftModule.InvokeVoidAsync("detach", DraftKey);
            await draftModule.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }
}
