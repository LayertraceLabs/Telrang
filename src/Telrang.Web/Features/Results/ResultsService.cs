using System.Text.Json;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Scoring;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Results;

/// <summary>
/// Results for one poll: one <see cref="QuestionResult"/> per question in definition order (DESIGN.md §34.5). A simple poll
/// (one question, no pages or groups) also has its question's set at the top (<see cref="Ranked"/>, <see cref="Points"/>,
/// <see cref="Dates"/>), as before §34; a structured poll leaves those null.
/// </summary>
/// <param name="VoterCount">Responses: in a simple ranked, points or dates poll the count of its result set (as before §34),
/// else every stored response.</param>
/// <param name="PointsDistribution">Points polls: option key -> voters per <see cref="PointsBuckets"/> bucket.</param>
public sealed record PollResults(int VoterCount, RankedResultSet? Ranked, PointsResultSet? Points,
    IReadOnlyDictionary<string, int[]>? PointsDistribution = null)
{
    /// <summary>Dates polls: yes/maybe/no per option, chronological.</summary>
    public DatesResultSet? Dates { get; init; }

    /// <summary>Every question's results, in definition order.</summary>
    public IReadOnlyList<QuestionResult> Questions { get; init; } = [];
}

/// <summary>
/// One question's results; exactly one of the per-type sets is filled. Never any free text: a text question is a count, a
/// choice question's Other a count (DESIGN.md §34.3). The texts are <see cref="ResultsService.TextsAsync"/>, owner-only.
/// </summary>
/// <param name="AnsweredCount">Responses with a part for the question (in a simple ranked, points or dates poll every response).</param>
public sealed record QuestionResult(PollQuestion Question, int AnsweredCount)
{
    public RankedResultSet? Ranked { get; init; }
    public PointsResultSet? Points { get; init; }

    /// <summary>Points: option key -> voters per <see cref="PointsBuckets"/> bucket.</summary>
    public IReadOnlyDictionary<string, int[]>? PointsDistribution { get; init; }
    public DatesResultSet? Dates { get; init; }
    public ChoiceResultSet? Choice { get; init; }
    public NumberResultSet? Number { get; init; }
    public TextResultSet? Text { get; init; }
}

/// <summary>
/// The owner's free text of one question (DESIGN.md §34.3): a text question's answers or a choice question's Other texts
/// (<see cref="Other"/>), one page (<see cref="Window"/>) in submission order, decrypted for that page only. A null entry is a
/// response whose text could not be read (or an Other picked without a text).
/// </summary>
public sealed record TextAnswers(PollQuestion Question, bool Other, RowWindow Window, IReadOnlyList<string?> Texts);

/// <summary>Query parameters of the results page.</summary>
public static class ResultsQuery
{
    /// <summary>The page of the ballot table (<c>?rows=2</c>).</summary>
    public const string Rows = "rows";

    /// <summary>Which view (<c>?view=ballots</c>, DESIGN.md §33); anything else is the summary.</summary>
    public const string View = "view";

    public const string BallotsView = "ballots";

    /// <summary>The question whose free text <see cref="Rows"/> pages on the summary (<c>?texts=q4&amp;rows=2</c>); the others show page 1.</summary>
    public const string Texts = "texts";
}

/// <summary>One page of a ballot table (DESIGN.md §31): page <see cref="Page"/> of <see cref="Pages"/>, rows
/// <see cref="First"/> to <see cref="Last"/> (1-based) of <see cref="Total"/>.</summary>
public sealed record RowWindow(int Page, int Pages, int First, int Last, int Total)
{
    /// <summary>Rows per page of every ballot table.</summary>
    public const int PageSize = 50;

    /// <summary>The window of page <paramref name="page"/> (clamped to the pages there are) over <paramref name="total"/> rows.</summary>
    public static RowWindow Of(int total, int page)
    {
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 1, pages);
        var first = (page - 1) * PageSize;
        return new RowWindow(page, pages, total == 0 ? 0 : first + 1, Math.Min(total, first + PageSize), total);
    }

    /// <summary>0-based index of the first row and how many rows the page holds.</summary>
    public (int Skip, int Take) Range => (Math.Max(0, First - 1), Last - First + (Total == 0 ? 0 : 1));
}

/// <summary>
/// A poll's computed results and its responses in compact form, as cached per poll (<see cref="ResultsCache"/>):
/// <see cref="Summary"/> without rows, and per response the name, alias and its answers as numbers laid out by
/// <see cref="Layout"/>. Pages build the rows they show from it. Never any free text (DESIGN.md §34.3).
/// </summary>
public sealed record PollResultsData(string DefinitionJson, PollResults Summary, IReadOnlyList<ResponseCells> Responses)
{
    /// <summary>Where each question's cells sit in <see cref="ResponseCells.Cells"/>.</summary>
    public CellLayout Layout { get; init; } = CellLayout.Empty;

    /// <summary>Rough size in memory, for the cache's size limit.</summary>
    public long EstimatedBytes { get; init; }
}

/// <param name="BallotId">The stored ballot, for the owner's free text of a page of responses (never shown).</param>
/// <param name="Cells">The answers, per question at its <see cref="CellLayout"/> offset: one number per option (ranked: the
/// position, -1 banned, 0 unranked; points: the points; dates: the <see cref="Availability"/>; choice: 1 picked, then one
/// for Other), text and number: 1 when answered. A simple poll's question starts at 0, so its cells are as before §34.</param>
/// <param name="Numbers">One value per number question (<see cref="CellLayout.NumberIndex"/>); 0 when unanswered.</param>
public sealed record ResponseCells(string? DisplayName, string? Alias, int[] Cells)
{
    public Guid BallotId { get; init; }
    public decimal[] Numbers { get; init; } = [];
}

/// <summary>Where each question's answers sit in a <see cref="ResponseCells"/> (see there).</summary>
public sealed class CellLayout
{
    public static readonly CellLayout Empty = new([], [], 0, 0);

    private CellLayout(int[] offsets, int[] numberIndex, int width, int numbers)
    {
        Offsets = offsets;
        NumberIndex = numberIndex;
        Width = width;
        Numbers = numbers;
    }

    /// <summary>Per question (definition order) the index of its first cell.</summary>
    public IReadOnlyList<int> Offsets { get; }

    /// <summary>Per question its index in <see cref="ResponseCells.Numbers"/>; -1 unless a number question.</summary>
    public IReadOnlyList<int> NumberIndex { get; }

    public int Width { get; }
    public int Numbers { get; }

    /// <summary>How many cells a question takes.</summary>
    public static int WidthOf(PollQuestion q) => q.Type switch
    {
        PollType.Ranked or PollType.Points or PollType.Dates => q.Options.Count,
        PollType.Choice => q.Options.Count + 1,
        PollType.Text or PollType.Number => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(q), q.Type, null),
    };

    public static CellLayout Of(PollDefinition def)
    {
        var offsets = new int[def.Questions.Count];
        var numbers = new int[def.Questions.Count];
        int width = 0, count = 0;
        for (var i = 0; i < def.Questions.Count; i++)
        {
            offsets[i] = width;
            width += WidthOf(def.Questions[i]);
            numbers[i] = def.Questions[i].Type == PollType.Number ? count++ : -1;
        }
        return new CellLayout(offsets, numbers, width, count);
    }

    /// <summary>Question <paramref name="index"/>'s cells of <paramref name="r"/>.</summary>
    public ReadOnlySpan<int> Slice(ResponseCells r, int index, PollQuestion q) => r.Cells.AsSpan(Offsets[index], WidthOf(q));
}

/// <summary>
/// The ballots view (DESIGN.md §33): one page (<see cref="Window"/>) of the responses in submission order, named for
/// the viewer (the owner: alias, else the name as entered; voters: the name as entered only; blank: "Anonymous"). No
/// times, no identities. Exactly one of the per-type sets is filled.
/// </summary>
public sealed record BallotTable(RowWindow Window)
{
    /// <summary>Ranked polls: one column per ballot.</summary>
    public IReadOnlyList<RankedBallotColumn>? Ranked { get; init; }

    /// <summary>Points polls: the options (rows) in results order, one column per ballot.</summary>
    public PointsBallotTable? Points { get; init; }

    /// <summary>Dates polls ("Who can when"): one row per ballot.</summary>
    public IReadOnlyList<DatesGridRow>? Dates { get; init; }

    /// <summary>
    /// Every other poll (several questions, pages or groups, or one choice, text or number question; DESIGN.md §34.5): one row
    /// per response, one column per question, each cell the answer in its text form (<see cref="AnswerText"/>).
    /// </summary>
    public ResponseTable? Responses { get; init; }
}

/// <param name="Questions">The columns, in definition order.</param>
public sealed record ResponseTable(IReadOnlyList<PollQuestion> Questions, IReadOnlyList<ResponseRow> Rows);

/// <param name="Cells">One per question of <see cref="ResponseTable.Questions"/>; empty when not answered.</param>
public sealed record ResponseRow(string Name, IReadOnlyList<string> Cells);

/// <param name="Ranked">The labels of the ranked options, first choice first.</param>
/// <param name="Banned">The labels of the banned options, in the definition's order.</param>
public sealed record RankedBallotColumn(string Name, IReadOnlyList<string> Ranked, IReadOnlyList<string> Banned);

/// <param name="Options">Option labels in results order.</param>
/// <param name="Columns">Per ballot the points, one per entry of <paramref name="Options"/>.</param>
public sealed record PointsBallotTable(IReadOnlyList<string> Options, IReadOnlyList<PointsBallotColumn> Columns);

public sealed record PointsBallotColumn(string Name, IReadOnlyList<int> Points);

/// <param name="Name">The owner's alias when set, else the display name as entered (voters: the name only); "Anonymous"
/// when blank.</param>
/// <param name="Answers">One answer per option, in the definition's option order.</param>
public sealed record DatesGridRow(string Name, IReadOnlyList<Availability> Answers)
{
    public const string AnonymousName = "Anonymous";
}

/// <summary>
/// Five buckets of points per voter for one option, relative to the most one voter may give
/// (the cap per option, else the budget): 0, 1–25%, 26–50%, 51–75%, 76–100%.
/// </summary>
public static class PointsBuckets
{
    public const int Count = 5;

    public static int Cap(PollDefinition def) => Cap(def.Questions[0]);

    public static int Cap(PollQuestion q) => Math.Max(1, Math.Min(q.MaxPerOption ?? q.Budget ?? 1, q.Budget ?? int.MaxValue));

    /// <summary>Bucket index for one voter's value: 0 for zero, else the quarter of <paramref name="cap"/> it falls in.</summary>
    public static int Of(int value, int cap)
    {
        if (value <= 0) return 0;
        for (var q = 1; q <= 3; q++)
        {
            if ((long)value * 4 <= (long)q * cap) return q;
        }
        return 4;
    }

    public static int[] FromHistogram(IReadOnlyDictionary<int, int> histogram, int cap)
    {
        var buckets = new int[Count];
        foreach (var (value, voters) in histogram) buckets[Of(value, cap)] += voters;
        return buckets;
    }

    /// <summary>Human label with the actual point range, e.g. "1–15" for the first quarter of 60.</summary>
    public static string Label(int bucket, int cap)
    {
        if (bucket == 0) return "0";
        var low = (int)((long)(bucket - 1) * cap / 4) + 1;
        var high = (int)((long)bucket * cap / 4);
        if (bucket == 4) high = cap;
        return low > high ? "–" : low == high ? $"{low}" : $"{low}–{high}";
    }

    public static readonly string[] PercentLabels = ["0", "1–25%", "26–50%", "51–75%", "76–100%"];
}


/// <summary>
/// Computes results on read from the raw ballots (never stored in the database); one computation per poll is kept in
/// memory until the poll changes (<see cref="ResultsCache"/>, DESIGN.md §31). Every question is scored in one pass over the
/// ballots (one parse per payload, DESIGN.md §34.5). That pass never selects or decrypts <c>Ballot.TextCipher</c>: free text
/// is read only by <see cref="TextsAsync"/> and the owner's <see cref="BallotsAsync"/>, a page at a time (§34.3).
/// </summary>
public sealed class ResultsService(IDbContextFactory<ApplicationDbContext> dbFactory, ResultsCache cache, ResponseTextProtection textProtection)
{
    /// <summary>Requires <see cref="PollAccess.CanSeeResults"/>; throws otherwise so a page bug cannot leak results.</summary>
    public async Task<PollResults> ComputeAsync(PollAccess access, CancellationToken ct = default) =>
        (await DataAsync(access, ct)).Summary;

    /// <summary>Whether <paramref name="access"/> may open the ballots view (DESIGN.md §33): the owner always, voters
    /// with <see cref="PollAccess.CanSeeBallots"/>.</summary>
    public static bool CanSeeBallotTable(PollAccess access) => access.CanSeeResults && (access.IsOwner || access.CanSeeBallots);

    /// <summary>
    /// The ballots view and the ballots CSV show one row per response and one column per question (DESIGN.md §34.5): every
    /// poll but a simple ranked, points or dates poll, which keeps its §33 layout and CSV columns.
    /// </summary>
    public static bool UsesResponseTable(PollDefinition def) =>
        !(VoteLayout.IsSimple(def) && def.Questions[0].Type is PollType.Ranked or PollType.Points or PollType.Dates);

    /// <summary>The poll has questions with free text: a text question or a choice question with Other.</summary>
    public static bool HasFreeText(PollDefinition def) =>
        def.Questions.Any(q => q.Type == PollType.Text || q is { Type: PollType.Choice, AllowOther: true });

    /// <summary>
    /// Page <paramref name="page"/> (1-based, clamped; <see cref="RowWindow.PageSize"/> ballots) of the ballots view, or
    /// null when <see cref="CanSeeBallotTable"/> refuses. Aliases and free text only for the owner (DESIGN.md §20.3, §34.3):
    /// voters see "answered" for a text answer and "Other" for an Other text.
    /// </summary>
    public async Task<BallotTable?> BallotsAsync(PollAccess access, int page, CancellationToken ct = default)
    {
        if (!CanSeeBallotTable(access)) return null;
        var data = await DataAsync(access, ct);
        var def = access.Definition;
        var window = RowWindow.Of(data.Responses.Count, page);
        var (skip, take) = window.Range;
        var shown = data.Responses.Skip(skip).Take(take).ToList();
        Func<ResponseCells, string> name = access.IsOwner
            ? r => string.IsNullOrWhiteSpace(r.Alias) ? VoterName(r.DisplayName) : r.Alias
            : r => VoterName(r.DisplayName);
        if (UsesResponseTable(def))
        {
            var owner = access.IsOwner;
            var texts = owner && HasFreeText(def) ? await RevealAsync(access.Poll.Id, shown.Select(r => r.BallotId), ct) : null;
            var rows = shown.Select(r =>
            {
                var free = texts?.GetValueOrDefault(r.BallotId);
                var cells = new List<string>(def.Questions.Count);
                for (var i = 0; i < def.Questions.Count; i++)
                    cells.Add(AnswerText.Cell(def.Questions[i], data.Layout, i, r, owner, free, AnswerText.Form.View));
                return new ResponseRow(name(r), cells);
            }).ToList();
            return new BallotTable(window) { Responses = new ResponseTable(def.Questions, rows) };
        }
        var q = def.Questions[0];
        switch (q.Type)
        {
            case PollType.Ranked:
                return new BallotTable(window)
                {
                    Ranked = shown.Select(r => new RankedBallotColumn(name(r),
                        r.Cells.Select((c, i) => (c, i)).Where(x => x.c > 0).OrderBy(x => x.c).Select(x => q.Options[x.i].Label).ToList(),
                        r.Cells.Select((c, i) => (c, i)).Where(x => x.c < 0).Select(x => q.Options[x.i].Label).ToList())).ToList(),
                };
            case PollType.Points:
            {
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < q.Options.Count; i++) index[q.Options[i].Key] = i;
                var order = data.Summary.Points!.Rows.Where(x => index.ContainsKey(x.Key)).ToList();
                return new BallotTable(window)
                {
                    Points = new PointsBallotTable(order.Select(x => x.Label).ToList(),
                        shown.Select(r => new PointsBallotColumn(name(r), order.Select(x => r.Cells[index[x.Key]]).ToList())).ToList()),
                };
            }
            case PollType.Dates:
                return new BallotTable(window) { Dates = Grid(shown, name) };
            case PollType.Choice:
            case PollType.Text:
            case PollType.Number:
                throw new System.Diagnostics.UnreachableException("UsesResponseTable covers these types.");
            default:
                throw new ArgumentOutOfRangeException(nameof(access), q.Type, null);
        }
    }

    /// <summary>
    /// The owner's free text (DESIGN.md §34.3): per text question its answers, per choice question with Other its Other texts,
    /// in definition order, one page each (<paramref name="page"/> for <paramref name="questionKey"/>, page 1 for the
    /// others), decrypted for those pages only. Empty for anyone but the owner, whatever the results and ballots settings.
    /// </summary>
    public async Task<IReadOnlyList<TextAnswers>> TextsAsync(PollAccess access, string? questionKey, int page, CancellationToken ct = default)
    {
        if (!access.IsOwner || !HasFreeText(access.Definition)) return [];
        var data = await DataAsync(access, ct);
        var def = access.Definition;
        var pages = new List<(PollQuestion Q, bool Other, RowWindow Window, List<ResponseCells> Rows)>();
        for (var i = 0; i < def.Questions.Count; i++)
        {
            var q = def.Questions[i];
            int flag;
            if (q.Type == PollType.Text) flag = data.Layout.Offsets[i];
            else if (q is { Type: PollType.Choice, AllowOther: true }) flag = data.Layout.Offsets[i] + q.Options.Count;
            else continue;
            var answered = data.Responses.Where(r => r.Cells[flag] != 0).ToList();
            var window = RowWindow.Of(answered.Count, string.Equals(q.Key, questionKey, StringComparison.Ordinal) ? page : 1);
            var (skip, take) = window.Range;
            pages.Add((q, q.Type == PollType.Choice, window, answered.Skip(skip).Take(take).ToList()));
        }
        var texts = await RevealAsync(access.Poll.Id, pages.SelectMany(p => p.Rows.Select(r => r.BallotId)), ct);
        return pages.Select(p => new TextAnswers(p.Q, p.Other, p.Window, p.Rows.Select(r =>
        {
            var t = texts.GetValueOrDefault(r.BallotId)?.GetValueOrDefault(p.Other ? ResponseText.OtherKey(p.Q) : ResponseText.AnswerKey(p.Q));
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }).ToList())).ToList();
    }

    /// <summary>
    /// The owner's computed data (the ballots CSV): the responses in submission order with their cells and ballot ids. Throws
    /// unless <paramref name="access"/> is the owner's.
    /// </summary>
    internal Task<PollResultsData> OwnerDataAsync(PollAccess access, CancellationToken ct)
    {
        if (!access.IsOwner) throw new InvalidOperationException("Only the owner reads the responses with their free text.");
        return DataAsync(access, ct);
    }

    /// <summary>The free text of the given ballots of the poll, decrypted; a null value is a cipher that cannot be read.</summary>
    internal async Task<Dictionary<Guid, IReadOnlyDictionary<string, string>?>> RevealAsync(Guid pollId, IEnumerable<Guid> ballotIds, CancellationToken ct)
    {
        var ids = ballotIds.Distinct().ToList();
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string>?>();
        if (ids.Count == 0) return result;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Ballots.AsNoTracking().Where(b => b.PollId == pollId && ids.Contains(b.Id))
            .Select(b => new { b.Id, b.TextCipher }).ToListAsync(ct);
        foreach (var r in rows) result[r.Id] = textProtection.RevealTexts(pollId, r.TextCipher);
        return result;
    }

    private async Task<PollResultsData> DataAsync(PollAccess access, CancellationToken ct)
    {
        if (!access.CanSeeResults) throw new InvalidOperationException("Caller may not see results for this poll.");
        var json = access.Poll.DefinitionJson;
        var pollId = access.Poll.Id;
        var def = access.Definition;
        return await cache.GetAsync(pollId, json, c => LoadAsync(pollId, def, json, c), ct);
    }

    /// <summary>Reads every ballot of the poll once (never its free text) and computes the results and the compact rows.</summary>
    private async Task<PollResultsData> LoadAsync(Guid pollId, PollDefinition def, string definitionJson, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = (await db.Ballots.AsNoTracking().Where(b => b.PollId == pollId)
                .Select(b => new Row(b.Id, b.PayloadJson, b.DisplayName, b.Alias, b.SubmittedAt, b.ResponseNo)).ToListAsync(ct))
            // DateTimeOffset ordering in memory: SQLite cannot ORDER BY it.
            .OrderBy(b => b.SubmittedAt).ThenBy(b => b.ResponseNo).ToList();
        return Score(def, definitionJson, rows);
    }

    private sealed record Row(Guid Id, string PayloadJson, string? DisplayName, string? Alias, DateTimeOffset SubmittedAt, int ResponseNo);

    /// <summary>
    /// One pass over the responses: each payload is parsed once and every question's part read from it. In a simple ranked,
    /// points or dates poll every response counts (a missing part is an empty ballot, as before §34); otherwise a question
    /// counts the responses with a part (§34.3: a missing key is "not answered").
    /// </summary>
    private static PollResultsData Score(PollDefinition def, string definitionJson, List<Row> rows)
    {
        var qs = def.Questions;
        var n = qs.Count;
        var simple = VoteLayout.IsSimple(def);
        var layout = CellLayout.Of(def);
        var known = qs.Select(BallotFilter.For).ToArray();
        var index = qs.Select(q =>
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < q.Options.Count; i++) d[q.Options[i].Key] = i;
            return d;
        }).ToArray();
        var ranked = new List<RankedBallot>[n];
        var points = new List<PointsBallot>[n];
        var dates = new List<DatesBallot>[n];
        var choice = new List<ChoiceBallot>[n];
        var number = new List<NumberBallot>[n];
        var texts = new int[n];
        for (var i = 0; i < n; i++)
        {
            ranked[i] = [];
            points[i] = [];
            dates[i] = [];
            choice[i] = [];
            number[i] = [];
        }
        var responses = new List<ResponseCells>(rows.Count);
        foreach (var row in rows)
        {
            var cells = new int[layout.Width];
            var numbers = layout.Numbers == 0 ? [] : new decimal[layout.Numbers];
            using (var doc = JsonDocument.Parse(row.PayloadJson))
            {
                var root = doc.RootElement;
                for (var i = 0; i < n; i++)
                {
                    var q = qs[i];
                    var o = layout.Offsets[i];
                    var has = PollJson.TryPart(root, q.Key, out var part);
                    switch (q.Type)
                    {
                        case PollType.Ranked:
                        {
                            if (!has && !simple) break;
                            var b = known[i].Filter(has ? PollJson.RankedFrom(part) : new RankedBallot([], []));
                            ranked[i].Add(b);
                            foreach (var k in b.Banned) cells[o + index[i][k]] = -1;
                            for (var r = 0; r < b.Ranked.Count; r++) cells[o + index[i][b.Ranked[r]]] = r + 1;
                            break;
                        }
                        case PollType.Points:
                        {
                            if (!has && !simple) break;
                            var b = known[i].Filter(has ? PollJson.PointsFrom(part) : new PointsBallot(new Dictionary<string, int>()));
                            points[i].Add(b);
                            foreach (var (k, v) in b.Points) cells[o + index[i][k]] = v;
                            break;
                        }
                        case PollType.Dates:
                        {
                            if (!has && !simple) break;
                            var b = known[i].Filter(has ? PollJson.DatesFrom(part) : new DatesBallot(new Dictionary<string, Availability>()));
                            dates[i].Add(b);
                            foreach (var (k, v) in b.Answers) cells[o + index[i][k]] = (int)v;
                            break;
                        }
                        case PollType.Choice:
                        {
                            if (!has) break;
                            var b = known[i].Filter(PollJson.ChoiceFrom(part));
                            if (b.Other && !q.AllowOther) b = b with { Other = false };
                            choice[i].Add(b);
                            foreach (var k in b.Keys) cells[o + index[i][k]] = 1;
                            if (b.Other) cells[o + q.Options.Count] = 1;
                            break;
                        }
                        case PollType.Text:
                            // The part only says "answered"; the text is in TextCipher, which this pass never reads.
                            if (!has) break;
                            texts[i]++;
                            cells[o] = 1;
                            break;
                        case PollType.Number:
                            if (!has || PollJson.NumberFrom(part) is not { } nb) break;
                            number[i].Add(nb);
                            cells[o] = 1;
                            numbers[layout.NumberIndex[i]] = nb.Value;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(def), q.Type, null);
                    }
                }
            }
            responses.Add(new ResponseCells(row.DisplayName, row.Alias, cells) { BallotId = row.Id, Numbers = numbers });
        }

        var results = new QuestionResult[n];
        for (var i = 0; i < n; i++)
        {
            var q = qs[i];
            switch (q.Type)
            {
                case PollType.Ranked:
                {
                    var set = RankedResults.Compute(q, ranked[i]);
                    results[i] = new QuestionResult(q, set.VoterCount) { Ranked = set };
                    break;
                }
                case PollType.Points:
                {
                    var set = PointsResults.Compute(q, points[i]);
                    results[i] = new QuestionResult(q, set.VoterCount) { Points = set, PointsDistribution = Distribution(q, points[i]) };
                    break;
                }
                case PollType.Dates:
                {
                    var set = DatesResults.Compute(q, dates[i]);
                    results[i] = new QuestionResult(q, set.VoterCount) { Dates = set };
                    break;
                }
                case PollType.Choice:
                {
                    var set = ChoiceResults.Compute(q, choice[i]);
                    results[i] = new QuestionResult(q, set.RespondentCount) { Choice = set };
                    break;
                }
                case PollType.Number:
                {
                    var set = NumberResults.Compute(q, number[i]);
                    results[i] = new QuestionResult(q, set.Count) { Number = set };
                    break;
                }
                case PollType.Text:
                    // Counted from the payload's "answered" parts (BallotService stores one only for a non-blank text), not
                    // from the texts: TextResults.Compute would need them decrypted.
                    results[i] = new QuestionResult(q, texts[i]) { Text = new TextResultSet(texts[i]) };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(def), q.Type, null);
            }
        }
        var first = results[0];
        var summary = simple && first.Question.Type is PollType.Ranked or PollType.Points or PollType.Dates
            ? new PollResults(first.AnsweredCount, first.Ranked, first.Points, first.PointsDistribution) { Dates = first.Dates, Questions = results }
            : new PollResults(rows.Count, null, null) { Questions = results };
        var bytes = 4096L + qs.Sum(q => q.Options.Count * 256L + 256L)
                    + responses.Sum(r => 112L + r.Cells.Length * 4L + r.Numbers.Length * 16L
                                         + 2L * ((r.DisplayName?.Length ?? 0) + (r.Alias?.Length ?? 0)));
        return new PollResultsData(definitionJson, summary, responses) { Layout = layout, EstimatedBytes = bytes };
    }

    private static Dictionary<string, int[]> Distribution(PollQuestion q, IReadOnlyList<PointsBallot> ballots)
    {
        var cap = PointsBuckets.Cap(q);
        return q.Options.ToDictionary(o => o.Key,
            o => PointsBuckets.FromHistogram(PointsResults.Histogram(ballots, o.Key), cap), StringComparer.Ordinal);
    }

    private static string VoterName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? DatesGridRow.AnonymousName : displayName;

    private static List<DatesGridRow> Grid(IEnumerable<ResponseCells> rows, Func<ResponseCells, string> name) =>
        rows.Select(r => new DatesGridRow(name(r), r.Cells.Select(c => (Availability)c).ToList())).ToList();

    /// <summary>Results of ballots already in hand (the home page examples, DESIGN.md §23.2).</summary>
    public static PollResults Ranked(PollDefinition def, IEnumerable<RankedBallot> ballots)
    {
        var set = RankedResults.Compute(def.Questions[0], ballots);
        return new PollResults(set.VoterCount, set, null) { Questions = [new QuestionResult(def.Questions[0], set.VoterCount) { Ranked = set }] };
    }

    /// <inheritdoc cref="Ranked(PollDefinition, IEnumerable{RankedBallot})"/>
    public static PollResults Points(PollDefinition def, IReadOnlyList<PointsBallot> ballots)
    {
        var q = def.Questions[0];
        var set = PointsResults.Compute(q, ballots);
        var distribution = Distribution(q, ballots);
        return new PollResults(set.VoterCount, null, set, distribution)
        {
            Questions = [new QuestionResult(q, set.VoterCount) { Points = set, PointsDistribution = distribution }],
        };
    }
}
