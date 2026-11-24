using System.Globalization;
using System.Text.RegularExpressions;
using Telrang.Core.Polls;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// The poll form (DESIGN.md §20.2, §34.5), used by the new and edit pages. Works on a <see cref="PollDraft"/>, validates it on
/// every change with <see cref="PollParsing.Validate"/> (the same call the server makes on save) and shows each
/// <see cref="DraftError"/> under its control. A simple poll gets the simple editor, anything with several questions, pages or
/// groups the structured one (outline, tabs); both edit questions with the one <see cref="QuestionEditor"/>. With a
/// <see cref="DraftKey"/> the draft is kept in the browser (wwwroot/js/draft.js) until saved.
/// </summary>
public sealed partial class PollEditor : IAsyncDisposable
{
    [Inject] private PollService Polls { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Time { get; set; } = default!;

    /// <summary>The starting state (blank, a copy, or the stored poll). The editor works on its own copy.</summary>
    [Parameter, EditorRequired] public PollDraft Initial { get; set; } = default!;

    /// <summary>On edit: the stored definition (edit rules: the settings and each stored question's type and rules are fixed).</summary>
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

    /// <summary>Raised after each change with the validation Result (definition or errors).</summary>
    [Parameter] public EventCallback<DraftResult> Validated { get; set; }

    /// <summary>
    /// The poll's look to start from (DESIGN.md §34.6, §34.7): the stored one on edit, the copied poll's on a copy, else the
    /// account's default look; null = the instance theme.
    /// </summary>
    [Parameter] public Branding.ThemeLook? InitialLook { get; set; }

    /// <summary>The admin setting "Poll looks" is on: the Look tab (structured) and section (simple) are shown.</summary>
    [Parameter] public bool ShowLook { get; set; }

    /// <summary>The signed-in owner, for their saved looks.</summary>
    [Parameter] public string? UserId { get; set; }

    /// <summary>The page's buttons and messages, rendered under the form.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// The browser draft key: <c>draft:&lt;userId&gt;:&lt;pollId|new&gt;</c>. Per account, so a draft made by one account
    /// is never offered to another on the same browser; null (no draft) without a user.
    /// </summary>
    public static string? DraftKeyFor(string? userId, string? encodedPollId) =>
        userId is null ? null : $"draft:{userId}:{encodedPollId ?? "new"}";

    private static int counter;
    private readonly string uid = "pe" + Interlocked.Increment(ref counter).ToString(CultureInfo.InvariantCulture);

    private PollDraft draft = new();
    private PollDraft? baseline;
    private EditorContext ctx = default!;
    private readonly HashSet<string> claimed = new(StringComparer.Ordinal);

    private bool descriptionPreview;

    /// <summary>The simple editor shows the question's text fields: the question has a text or help (left from a structured draft).</summary>
    private bool simpleText;

    private enum EditorTab { Questions, Voting, Look, Preview }
    private enum AddKind { Question, Group, Page }
    private EditorTab tab = EditorTab.Questions;

    /// <summary>The outline item the structured editor shows: a <see cref="QuestionDraft"/>, <see cref="PageDraft"/> or <see cref="GroupDraft"/>.</summary>
    private object? selected;

    // Drafts are records (value equality): render keys go by reference.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> keys = new();
    private object KeyOf(object o) => keys.GetValue(o, _ => new object());

    /// <summary>Fold-all state of the editor's option help (DESIGN.md §23.4).</summary>
    private readonly Telrang.Web.Components.Layout.HelpState help = new(Telrang.Web.Components.Layout.HelpState.Editor);

    private IJSObjectReference? draftModule;
    private bool restored;
    private string? lastJson;

    private PollLimits Limits => Polls.CurrentLimits;
    private DraftResult Result => ctx.Result;

    /// <summary>The draft as the form shows it now.</summary>
    public PollDraft Current => draft;

    private Branding.ThemeLook? look;

    /// <summary>The poll's look as the form shows it now (null = the instance theme). Not part of the draft: not in the browser draft.</summary>
    public Branding.ThemeLook? CurrentLook => look;

    private void LookChanged(Branding.ThemeLook? value) => look = value;

    /// <summary>The form can be saved: no field errors and no unparsed option text.</summary>
    public bool IsValid => Result.Success && !ctx.TextInvalid;

    protected override void OnInitialized()
    {
        ctx = new EditorContext
        {
            Uid = uid,
            Limits = Polls.CurrentLimits,
            Existing = Existing,
            Changed = EventCallback.Factory.Create<string>(this, ChangedAsync),
            Today = () => Today,
        };
        look = InitialLook;
        Load(Copy(Initial), asBaseline: true);
    }

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(Existing, ctx.Existing) || Existing is null) return;
        ctx.Existing = Existing; // a save reloaded the stored poll
        Validate();
    }

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
    /// On edit (DESIGN.md §26, §34.5) the fixed settings are the stored ones, also in a restored browser draft: the poll's
    /// settings, and for each question that is a stored one (by key; the one question of a one-question poll also without a
    /// key, as the validator matches it) its type, Required and the type's rules. The form shows them read-only, so nothing
    /// the editor sends can change them (the validator refuses it anyway). Questions added by the edit keep theirs.
    /// </summary>
    public static void PinFixed(PollDraft d, PollDefinition existing)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(existing);
        var fresh = PollDraft.FromDefinition(existing);
        d.VoterMode = fresh.VoterMode;
        d.Responses = fresh.Responses;
        d.Names = fresh.Names;
        d.ResultsVisibility = fresh.ResultsVisibility;
        d.ResultsTiming = fresh.ResultsTiming;
        d.ShowBallots = fresh.ShowBallots;
        d.TimeZone = fresh.TimeZone;
        if (existing.Questions.Count == 1 && d.Questions.Count == 1 && string.IsNullOrEmpty(d.Questions[0].Key))
            d.Questions[0].Key = existing.Questions[0].Key;
        var claimedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in d.Questions)
        {
            if (q.Key is null) continue;
            var f = fresh.Questions.FirstOrDefault(s => s.Key == q.Key);
            if (!claimedKeys.Add(q.Key)) { q.Key = null; continue; } // repeated: a new question
            if (f is null) continue; // not the poll's: the validator ignores it, the question is new
            (q.Type, q.Required) = (f.Type, f.Required);
            (q.Scoring, q.Bans, q.RankAll) = (f.Scoring, f.Bans, f.RankAll);
            (q.Budget, q.MaxPerOption, q.SpendAll, q.Maybe) = (f.Budget, f.MaxPerOption, f.SpendAll, f.Maybe);
            (q.MinSelect, q.MaxSelect, q.AllowOther, q.Display) = (f.MinSelect, f.MaxSelect, f.AllowOther, f.Display);
            (q.Multiline, q.MinLength, q.MaxLength) = (f.Multiline, f.MinLength, f.MaxLength);
            (q.Min, q.Max, q.Decimals, q.Unit) = (f.Min, f.Max, f.Decimals, f.Unit);
        }
    }

    private void Load(PollDraft d, bool asBaseline)
    {
        if (d.Questions.Count == 0) d.Questions.Add(new QuestionDraft());
        if (ctx.Existing is null) Normalise(d);
        else PinFixed(d, ctx.Existing);
        if (ctx.Existing is null && string.IsNullOrWhiteSpace(d.TimeZone)) d.TimeZone = DefaultTimeZone;
        foreach (var q in d.Questions) QuestionEditor.Prepare(q);
        draft = d;
        if (asBaseline) baseline = Copy(d);
        simpleText = DraftOutline.IsSimple(d) && (d.Questions[0].Prompt.Trim().Length > 0 || d.Questions[0].Help.Trim().Length > 0);
        selected = d.Questions[0];
        ctx.TextInvalid = false;
        ctx.RevealAll = ctx.Existing is not null;
        ctx.Touched.Clear();
        Validate();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !RendererInfo.IsInteractive) return;
        // Before the draft is attached: the browser's zone is part of the starting state (no unsaved change), and a
        // restored draft then replaces it with its own.
        if (BrowserTimeZone && Existing is null && await ApplyBrowserTimeZoneAsync())
        {
            await Validated.InvokeAsync(Result);
            StateHasChanged();
        }
        if (DraftKey is null) return;
        try
        {
            lastJson = PollJson.Serialize(draft);
            draftModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/draft.js");
            var stored = await draftModule.InvokeAsync<string?>("attach", DraftKey, lastJson, ReplaceDraft);
            if (stored is null) return;
            // A restored draft on edit gets the stored fixed values per question (PinFixed, in Load).
            if (PollJson.DeserializeDraft(stored) is { } restoredDraft && DraftFits(restoredDraft, Polls.CurrentLimits))
            {
                Load(restoredDraft, asBaseline: false);
                restored = true;
                lastJson = stored;
                await draftModule.InvokeVoidAsync("update", DraftKey, stored);
                await Validated.InvokeAsync(Result);
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
    /// A restored browser draft is used only within the limits (R2-POLLS-2, DESIGN.md §34.5): at most the admin's number of
    /// questions, pages and groups, at most <see cref="PollLimits.OptionsMax"/> options per question, and no text over twice
    /// its limit (a draft may hold text being shortened). Anything bigger was not made by this form; rendering it would cost
    /// the server per item and per character.
    /// </summary>
    public static bool DraftFits(PollDraft d, PollLimits limits)
    {
        static bool Within(string? s, int max) => (s?.Length ?? 0) <= 2 * max;
        if (!Within(d.Title, limits.TitleMax) || !Within(d.Description, limits.DescriptionMax) || (d.TimeZone?.Length ?? 0) > 100) return false;
        if (d.Questions is null || d.Pages is null || d.Groups is null) return false;
        if (d.Questions.Count > limits.QuestionsMax || d.Pages.Count > limits.PagesMax || d.Groups.Count > limits.GroupsMax) return false;
        static bool Layout(string? key, string? title, string? description, PollLimits l) =>
            (key?.Length ?? 0) <= ShortTextMax && Within(title, l.TitleMax) && Within(description, l.DescriptionMax);
        if (!d.Pages.All(p => p is not null && Layout(p.Key, p.Title, p.Description, limits))) return false;
        if (!d.Groups.All(g => g is not null && Layout(g.Key, g.Title, g.Description, limits))) return false;
        return d.Questions.All(q => q is not null && q.Options is not null && q.Options.Count <= limits.OptionsMax
                                    && Within(q.Prompt, limits.PromptMax) && Within(q.Help, limits.HelpMax)
                                    && Within(q.Unit, Telrang.Core.Polls.PollRules.UnitMax)
                                    && (q.Key?.Length ?? 0) <= ShortTextMax && (q.Page?.Length ?? 0) <= ShortTextMax && (q.Group?.Length ?? 0) <= ShortTextMax
                                    && q.Options.All(o => o is not null && Within(o.Label, limits.OptionLabelMax)
                                                          && Within(o.Description, limits.OptionDescriptionMax)));
    }

    /// <summary>Call after a successful save: the current form becomes the baseline and the browser draft is dropped.</summary>
    /// <param name="stored">On edit: the definition just saved (the fixed values and keys the form checks against from now on).</param>
    public async Task MarkSavedAsync(PollDraft? saved = null, PollDefinition? stored = null)
    {
        restored = false;
        if (stored is not null) ctx.Existing = stored;
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
        ctx.RevealAll = true;
        StateHasChanged();
        return IsValid;
    }

    private async Task DiscardDraftAsync()
    {
        Load(Copy(baseline ?? new PollDraft()), asBaseline: true);
        await MarkSavedAsync();
        await Validated.InvokeAsync(Result);
    }

    // ---- change handling ----

    private void Validate() =>
        ctx.Result = PollParsing.Validate(draft, Polls.CurrentLimits, MaxDurationDays, CreatedAt ?? Time.GetUtcNow(), ctx.Existing);

    /// <summary>After every edit: cut oversized text, mark the field touched, revalidate, tell the page, keep the browser draft.</summary>
    private async Task ChangedAsync(string field)
    {
        ClampTexts(draft, Polls.CurrentLimits);
        ctx.Touched.Add(field);
        Validate();
        await Validated.InvokeAsync(Result);
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
        foreach (var p in d.Pages ?? [])
        {
            if (p is null) continue;
            p.Title = Cut(p.Title, limits.TitleMax);
            p.Description = Cut(p.Description, limits.DescriptionMax);
        }
        foreach (var g in d.Groups ?? [])
        {
            if (g is null) continue;
            g.Title = Cut(g.Title, limits.TitleMax);
            g.Description = Cut(g.Description, limits.DescriptionMax);
        }
        foreach (var q in d.Questions)
        {
            if (q is null) continue;
            q.Prompt = Cut(q.Prompt, limits.PromptMax);
            q.Help = Cut(q.Help, limits.HelpMax);
            q.Unit = Cut(q.Unit, Telrang.Core.Polls.PollRules.UnitMax);
            if (q.Options is null) continue;
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
        // its size in bytes): the clamp above keeps a real one-question draft below it; a structured draft past it is not
        // kept in the browser.
        if (json.Length > Settings.LimitSettings.MaxCircuitMessageBytes) return;
        lastJson = json;
        try { await draftModule.InvokeVoidAsync("update", DraftKey, json); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    // ---- the outline (DESIGN.md §34.5) ----

    /// <summary>The selected item is in the draft (it may have gone with a removal or a reload); else the first question.</summary>
    private void EnsureSelection()
    {
        var present = selected switch
        {
            QuestionDraft q => DraftOutline.IndexOf(draft, q) >= 0,
            PageDraft p => draft.Pages.Any(x => ReferenceEquals(x, p)),
            GroupDraft g => draft.Groups.Any(x => ReferenceEquals(x, g)),
            _ => false,
        };
        if (!present) selected = draft.Questions[0];
    }

    private void Select(object item)
    {
        selected = item;
        tab = EditorTab.Questions;
    }

    /// <summary>Add question / group / page: the new question is selected, in the structured editor's Questions tab.</summary>
    private Task AddAsync(AddKind kind)
    {
        var at = selected as QuestionDraft;
        var onPage = selected as PageDraft;
        if (at is null && selected is GroupDraft g) at = DraftOutline.QuestionsOf(draft, g).LastOrDefault();
        var added = kind switch
        {
            AddKind.Question => DraftOutline.AddQuestion(draft, Limits, at, onPage),
            AddKind.Group => DraftOutline.AddGroup(draft, Limits, at, onPage),
            AddKind.Page => DraftOutline.AddPage(draft, Limits),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (added is null) return Task.CompletedTask;
        selected = added;
        tab = EditorTab.Questions;
        return ChangedAsync("questions");
    }

    private Task AddOnPageAsync(PageDraft p)
    {
        if (DraftOutline.AddQuestion(draft, Limits, onPage: p) is not { } added) return Task.CompletedTask;
        selected = added;
        return ChangedAsync("questions");
    }

    private Task OutlineAsync(Action change)
    {
        change();
        return ChangedAsync("questions");
    }

    private Task RemoveQuestionAsync(QuestionDraft q)
    {
        var i = DraftOutline.IndexOf(draft, q);
        if (!DraftOutline.RemoveQuestion(draft, q)) return Task.CompletedTask;
        selected = draft.Questions[Math.Clamp(i, 0, draft.Questions.Count - 1)];
        return AfterStructureChangeAsync();
    }

    private Task RemovePageAsync(PageDraft p)
    {
        var first = DraftOutline.QuestionsOn(draft, p).FirstOrDefault();
        DraftOutline.RemovePage(draft, p);
        selected = first ?? draft.Questions[0];
        return AfterStructureChangeAsync();
    }

    private Task RemoveGroupAsync(GroupDraft g)
    {
        var first = DraftOutline.QuestionsOf(draft, g).FirstOrDefault();
        DraftOutline.RemoveGroup(draft, g);
        selected = first ?? draft.Questions[0];
        return AfterStructureChangeAsync();
    }

    /// <summary>
    /// After a removal: back down to one question without pages or groups is the simple editor again (DESIGN.md §34.1), which
    /// shows the question's text fields while the question has text.
    /// </summary>
    private Task AfterStructureChangeAsync()
    {
        if (DraftOutline.IsSimple(draft))
        {
            var q = draft.Questions[0];
            simpleText = q.Prompt.Trim().Length > 0 || q.Help.Trim().Length > 0;
            tab = EditorTab.Questions;
        }
        return ChangedAsync("questions");
    }

    /// <summary>"4 pages · 9 questions" (DESIGN.md §34.1).</summary>
    private string SizeLine => PollText.SizeLine(Math.Max(1, draft.Pages.Count), draft.Questions.Count);

    // ---- errors ----

    /// <summary>The visible messages of one field; the field counts as placed (not repeated in the summary).</summary>
    private List<string> ErrorsOf(string field)
    {
        claimed.Add(field);
        return ctx.Errors(field);
    }

    private string Id(string field) => ctx.Id(field);
    private string? DescribedBy(string field, string? extra = null) => ctx.DescribedBy(field, extra);
    private string Invalid(string field) => ctx.Invalid(field);
    private string? AriaInvalid(string field) => ctx.AriaInvalid(field);
    private string Name(string group) => ctx.Name(group);

    [GeneratedRegex(@"^(questions|pages|groups)\[(\d+)\]")]
    private static partial Regex ItemPath();

    private bool QuestionHasErrors(int i) => ctx.ErrorsUnder($"questions[{i}]").Any();
    private bool PageHasErrors(int i) => ctx.ErrorsUnder($"pages[{i}]").Any();
    private bool GroupHasErrors(int i) => ctx.ErrorsUnder($"groups[{i}]").Any();

    /// <summary>The Voting and results tab holds a field with a visible error (shown on its tab).</summary>
    private bool TabErrors(EditorTab which) => which == EditorTab.Voting && Result.Errors.Any(e => ctx.Visible(e)
        && e.Field is "voters" or "responses" or "names" or "results" or "resultsTiming" or "showBallots" or "closesOn" or "timezone");

    /// <summary>
    /// Visible errors no control in view showed: the poll's own (e.g. "questions"), and those of the questions, pages and
    /// groups not shown now, each named ("Question 3: Enter a label."). The question a QuestionEditor shows lists its own.
    /// </summary>
    private IEnumerable<string> Unplaced(bool simple)
    {
        var shownQuestion = simple ? 0 : tab == EditorTab.Questions && selected is QuestionDraft q ? DraftOutline.IndexOf(draft, q) : -1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Result.Errors.Where(e => ctx.Visible(e) && !claimed.Contains(e.Field)))
        {
            string text;
            if (ItemPath().Match(e.Field) is { Success: true } m)
            {
                var n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (m.Groups[1].Value == "questions" && n == shownQuestion) continue;
                var what = m.Groups[1].Value switch { "questions" => "Question", "pages" => "Page", _ => "Group" };
                text = $"{what} {n + 1}: {e.Message}";
            }
            else text = e.Message;
            if (seen.Add(text)) yield return text;
        }
    }

    // ---- closing ----

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Time.GetUtcNow(), PollTime.Zone(draft.TimeZone ?? DefaultTimeZone)).DateTime);

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

    public async ValueTask DisposeAsync()
    {
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
