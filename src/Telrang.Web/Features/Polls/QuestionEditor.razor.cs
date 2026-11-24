using System.Globalization;
using Telrang.Core.Polls;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// One question of the poll form (DESIGN.md §34.5): the only editor of a question's text, type, rules and options, used by
/// the simple and the structured editor alike. It changes <see cref="Question"/> in place and reports every change through
/// <see cref="EditorContext.Changed"/>, which re-validates the whole draft and re-renders the form. Its own state (List or
/// Text, the calendar month, open details) lives as long as the question is shown: the form keys it by the question.
/// </summary>
public sealed partial class QuestionEditor : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public EditorContext Editor { get; set; } = default!;
    [Parameter, EditorRequired] public QuestionDraft Question { get; set; } = default!;

    /// <summary>The question's place in the draft (its field paths, <c>questions[i]</c>).</summary>
    [Parameter] public int Index { get; set; }

    /// <summary>The simple editor (DESIGN.md §34.1): the poll title is the question, so its text fields show only with <see cref="ShowText"/>.</summary>
    [Parameter] public bool Simple { get; set; }

    /// <summary>The simple editor shows the question's text fields anyway (the question has a text or help to clear).</summary>
    [Parameter] public bool ShowText { get; set; }

    private static int counter;
    private readonly string uid = "qe" + Interlocked.Increment(ref counter).ToString(CultureInfo.InvariantCulture);
    private readonly HashSet<string> claimed = new(StringComparer.Ordinal);

    private string Q => $"questions[{Index}]";
    private PollType Type => Question.Type;
    private PollLimits Limits => Editor.Limits;

    /// <summary>On edit, the stored question this one is (its type and rules are fixed); null for a new question.</summary>
    private PollQuestion? Stored => Editor.StoredOf(Question);

    private string TypeLabel => Simple ? "Poll type" : "Question type";

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
    private DotNetObjectReference<QuestionEditor>? selfRef;

    private static readonly string[] WeekdayLong = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    /// <summary>
    /// A question as the form edits it: a list type gets two empty options to start with, a dates question its options in
    /// date order. Run on every question the form loads or adds.
    /// </summary>
    public static void Prepare(QuestionDraft q)
    {
        ArgumentNullException.ThrowIfNull(q);
        if (q.Type == PollType.Dates) q.Options = SortedDates(q.Options);
        else if (Telrang.Core.Polls.PollRules.UsesOptions(q.Type) && q.Options.Count == 0) q.Options.AddRange([new OptionDraft(), new OptionDraft()]);
    }

    protected override void OnInitialized()
    {
        foreach (var o in Question.Options.Where(o => !string.IsNullOrWhiteSpace(o.Description))) detailsOpen.Add(o);
        month = FirstOfMonth(Question.Options.Where(o => o.Date is not null).Select(o => o.Date!.Value).DefaultIfEmpty(Editor.Today()).Min());
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
    }

    private Task Changed(string field) => Editor.Changed.InvokeAsync(field);

    // ---- errors (the form's, by field path) ----

    /// <summary>The visible messages of one field; the field counts as placed (not repeated at the end of the question).</summary>
    private List<string> ErrorsOf(string field)
    {
        claimed.Add(field);
        return Editor.Errors(field);
    }

    private string Id(string field) => Editor.Id(field);
    private string? DescribedBy(string field, string? extra = null) => Editor.DescribedBy(field, extra);
    private string Invalid(string field) => Editor.Invalid(field);
    private string? AriaInvalid(string field) => Editor.AriaInvalid(field);
    private string Name(string group) => uid + "-" + group;

    // ---- type ----

    /// <summary>
    /// Picks a type; Choice and Checkboxes are presets of the one choice type (DESIGN.md §34.3): at most one pick, or as many
    /// as there are options. The other family's options (dates or a list) wait in a stash: switching back restores them.
    /// </summary>
    private async Task SetTypeAsync(PollType type, bool checkboxes = false)
    {
        if (Stored is not null) return;
        var wasCheckboxes = Type == PollType.Choice && Question.MaxSelect > 1;
        if (type == Type && (type != PollType.Choice || checkboxes == wasCheckboxes)) return;
        var wasDates = Type == PollType.Dates;
        var isDates = type == PollType.Dates;
        if (wasDates != isDates)
        {
            if (wasDates) { stashDates = Question.Options; Question.Options = stashText ?? [new OptionDraft(), new OptionDraft()]; }
            else { stashText = Question.Options; Question.Options = stashDates ?? []; }
            textMode = false;
            SetTextErrors([]);
        }
        Question.Type = type;
        if (Telrang.Core.Polls.PollRules.UsesOptions(type) && type != PollType.Dates && Question.Options.Count == 0)
            Question.Options.AddRange([new OptionDraft(), new OptionDraft()]);
        if (type == PollType.Points && Question.Budget is null) Question.Budget = 100;
        if (type == PollType.Choice)
        {
            Question.MinSelect = 1;
            Question.MaxSelect = checkboxes ? Math.Max(2, ChoiceCount) : 1;
            if (checkboxes) Question.Display = ChoiceDisplay.Buttons;
        }
        if (textMode) ParseText();
        await Changed($"{Q}.type");
    }

    /// <summary>What Checkboxes counts as "every option": the options, and Other when offered.</summary>
    private int ChoiceCount => Question.Options.Count + (Question.AllowOther ? 1 : 0);

    /// <summary>A dropdown allows one pick: more picks show buttons (checkboxes).</summary>
    private Task SetMaxSelectAsync()
    {
        if (Question.MaxSelect > 1) Question.Display = ChoiceDisplay.Buttons;
        return Changed($"{Q}.maxSelect");
    }

    private Task SetDisplayAsync(ChoiceDisplay display)
    {
        if (Question.Display == display) return Task.CompletedTask;
        Question.Display = display;
        return Changed($"{Q}.display");
    }

    /// <summary>
    /// Checkboxes keep allowing every option while they did (DESIGN.md §34.3: the preset is "max = number of options"): when
    /// the options change and the maximum equalled the count before, it takes the new count. New questions only (on edit the
    /// maximum is fixed).
    /// </summary>
    private void FollowMax(int countBefore)
    {
        if (Stored is not null || Type != PollType.Choice || Question.MaxSelect <= 1 || Question.MaxSelect != countBefore) return;
        Question.MaxSelect = Math.Max(2, ChoiceCount);
    }

    // ---- options: list ----

    private string OptionField(int i, string f) => $"{Q}.options[{i}].{f}";

    /// <summary>The options limit of a dates question: the admin's, at most <see cref="Telrang.Core.Polls.PollRules.MaxDateOptions"/> (DESIGN.md §27).</summary>
    private int DatesMax => Telrang.Core.Polls.PollRules.OptionsLimit(PollType.Dates, Limits);

    /// <summary>No add (option, day, time) may take the list past the limit (R2-POLLS-2).</summary>
    private bool AtMax => Question.Options.Count >= Telrang.Core.Polls.PollRules.OptionsLimit(Type, Limits);

    private Task AddOption()
    {
        if (AtMax) return Task.CompletedTask;
        var before = ChoiceCount;
        var o = new OptionDraft();
        Question.Options.Add(o);
        FollowMax(before);
        focusOption = o;
        return Changed($"{Q}.options");
    }

    private Task MoveAsync(int i, int delta)
    {
        var list = Question.Options;
        var j = i + delta;
        if (j < 0 || j >= list.Count) return Task.CompletedTask;
        (list[i], list[j]) = (list[j], list[i]);
        return Changed($"{Q}.options");
    }

    private Task RemoveAsync(int i)
    {
        if (i < 0 || i >= Question.Options.Count) return Task.CompletedTask;
        var before = ChoiceCount;
        Question.Options.RemoveAt(i);
        FollowMax(before);
        return Changed($"{Q}.options");
    }

    // ---- options: text ----

    private void SetTextErrors(IReadOnlyList<OptionListError> errors)
    {
        textErrors = errors;
        Editor.TextInvalid = errors.Count > 0;
    }

    private async Task SetTextModeAsync(bool text)
    {
        if (text == textMode) return;
        if (text)
        {
            if (Type == PollType.Dates) Question.Options = SortedDates(Question.Options);
            optionsText = OptionListParser.Format(Question.Options.Where(o => Type == PollType.Dates ? o.Date is not null : true), Type);
            keysBeforeText = Question.Options.Where(o => o.Key is not null).Select(o => (LabelOf(o, Type), o.Key!)).ToList();
            SetTextErrors([]);
            moreTextErrors = 0;
            textMode = true;
        }
        else if (!TryLeaveText()) { segNonce++; return; }
        await Changed($"{Q}.options");
    }

    /// <summary>Text → list: only when the text parses; else the line errors stay in view.</summary>
    private bool TryLeaveText()
    {
        ParseText();
        if (textErrors.Count > 0) { Editor.Touched.Add($"{Q}.optionsText"); return false; }
        textMode = false;
        foreach (var o in Question.Options.Where(o => !string.IsNullOrWhiteSpace(o.Description))) detailsOpen.Add(o);
        if (Type == PollType.Dates) month = FirstOfMonth(Question.Options.Select(o => o.Date ?? Editor.Today()).DefaultIfEmpty(Editor.Today()).Min());
        return true;
    }

    private void ParseText()
    {
        var parsed = OptionListParser.Parse(optionsText, Type, Limits);
        SetTextErrors(parsed.Errors);
        moreTextErrors = parsed.MoreErrors;
        if (!parsed.Success) return;
        var before = ChoiceCount;
        Question.Options = parsed.Options.ToList();
        CarryKeys(Question.Options, Question.Options.Count == 0 ? [] : keysBeforeText, Type);
        FollowMax(before);
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
        var max = PollEditor.TextMargin * Math.Max(1, OptionListParser.MaxTextLength(Limits));
        if (optionsText.Length > max) optionsText = optionsText[..max];
        ParseText();
        return Changed($"{Q}.optionsText");
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
        return Changed($"{Q}.options");
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
            if (Editor.Result.Errors.Any(e => e.Field.StartsWith(prefix, StringComparison.Ordinal) && Editor.Visible(e)) || slotError.ContainsKey(d))
            {
                var page = DateEditing.PageOf(order, d);
                if (page >= 0) pages.Add(page);
            }
        }
        return pages;
    }

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private static List<OptionDraft> SortedDates(List<OptionDraft> options) =>
        options.OrderBy(o => o.Date).ThenBy(o => o.Start).ThenBy(o => o.End).ToList();

    private void SortDates() => Question.Options = SortedDates(Question.Options);

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
        slotInput[day] = text.Length <= PollEditor.ShortTextMax ? text : text[..PollEditor.ShortTextMax];
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
        return Changed($"{Q}.options");
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
        return Changed($"{Q}.options");
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
        return Changed($"{Q}.options");
    }

    private static string DayDescription(IEnumerable<OptionDraft> options) =>
        options.Select(o => o.Description).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";

    private Task SetDayDescriptionAsync(DateOnly day, string? text)
    {
        foreach (var o in Question.Options.Where(o => o.Date == day)) o.Description = text ?? "";
        return Changed($"{Q}.options");
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

    public async ValueTask DisposeAsync()
    {
        if (textErrors.Count > 0) Editor.TextInvalid = false; // the unparsed text goes with the editor; the options stay as last parsed
        if (selectModule is not null)
        {
            try { await selectModule.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
        }
        selfRef?.Dispose();
    }
}
