using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Scoring;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Results;

/// <summary>Results for one poll; exactly one of the per-type sets is filled.</summary>
/// <param name="PointsDistribution">Points polls: option key -> voters per <see cref="PointsBuckets"/> bucket.</param>
public sealed record PollResults(int VoterCount, RankedResultSet? Ranked, PointsResultSet? Points,
    IReadOnlyDictionary<string, int[]>? PointsDistribution = null)
{
    /// <summary>Dates polls: yes/maybe/no per option, chronological.</summary>
    public DatesResultSet? Dates { get; init; }
}

/// <summary>Query parameters of the results page.</summary>
public static class ResultsQuery
{
    /// <summary>The page of the ballot table (<c>?rows=2</c>).</summary>
    public const string Rows = "rows";

    /// <summary>Which view (<c>?view=ballots</c>, DESIGN.md §33); anything else is the summary.</summary>
    public const string View = "view";

    public const string BallotsView = "ballots";
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
/// <see cref="Summary"/> without rows, and per response the name, alias and one number per option (ranked: the position,
/// -1 banned, 0 unranked; points: the points; dates: the <see cref="Availability"/>). Pages build the rows they show from it.
/// </summary>
public sealed record PollResultsData(string DefinitionJson, PollResults Summary, IReadOnlyList<ResponseCells> Responses)
{
    /// <summary>Rough size in memory, for the cache's size limit.</summary>
    public long EstimatedBytes { get; init; }
}

/// <param name="Cells">One number per option in the definition's order (see <see cref="PollResultsData"/>).</param>
public sealed record ResponseCells(string? DisplayName, string? Alias, int[] Cells);

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
}

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

    public static int Cap(PollDefinition def) => Math.Max(1, Math.Min(def.Questions[0].MaxPerOption ?? def.Questions[0].Budget ?? 1, def.Questions[0].Budget ?? int.MaxValue));

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
/// memory until the poll changes (<see cref="ResultsCache"/>, DESIGN.md §31).
/// </summary>
public sealed class ResultsService(IDbContextFactory<ApplicationDbContext> dbFactory, ResultsCache cache)
{
    /// <summary>Requires <see cref="PollAccess.CanSeeResults"/>; throws otherwise so a page bug cannot leak results.</summary>
    public async Task<PollResults> ComputeAsync(PollAccess access, CancellationToken ct = default) =>
        (await DataAsync(access, ct)).Summary;

    /// <summary>Whether <paramref name="access"/> may open the ballots view (DESIGN.md §33): the owner always, voters
    /// with <see cref="PollAccess.CanSeeBallots"/>.</summary>
    public static bool CanSeeBallotTable(PollAccess access) => access.CanSeeResults && (access.IsOwner || access.CanSeeBallots);

    /// <summary>
    /// Page <paramref name="page"/> (1-based, clamped; <see cref="RowWindow.PageSize"/> ballots) of the ballots view, or
    /// null when <see cref="CanSeeBallotTable"/> refuses. Aliases only for the owner (DESIGN.md §20.3).
    /// </summary>
    public async Task<BallotTable?> BallotsAsync(PollAccess access, int page, CancellationToken ct = default)
    {
        if (!CanSeeBallotTable(access)) return null;
        var data = await DataAsync(access, ct);
        var q = access.Definition.Questions[0];
        var window = RowWindow.Of(data.Responses.Count, page);
        var (skip, take) = window.Range;
        var shown = data.Responses.Skip(skip).Take(take).ToList();
        Func<ResponseCells, string> name = access.IsOwner
            ? r => string.IsNullOrWhiteSpace(r.Alias) ? VoterName(r.DisplayName) : r.Alias
            : r => VoterName(r.DisplayName);
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
            default:
                throw new ArgumentOutOfRangeException(nameof(access), q.Type, null);
        }
    }

    private async Task<PollResultsData> DataAsync(PollAccess access, CancellationToken ct)
    {
        if (!access.CanSeeResults) throw new InvalidOperationException("Caller may not see results for this poll.");
        var json = access.Poll.DefinitionJson;
        var pollId = access.Poll.Id;
        var def = access.Definition;
        return await cache.GetAsync(pollId, json, c => LoadAsync(pollId, def, json, c), ct);
    }

    /// <summary>Reads every ballot of the poll once and computes the results and the compact rows.</summary>
    private async Task<PollResultsData> LoadAsync(Guid pollId, PollDefinition def, string definitionJson, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = (await db.Ballots.AsNoTracking().Where(b => b.PollId == pollId)
                .Select(b => new Row(b.PayloadJson, b.DisplayName, b.Alias, b.SubmittedAt, b.ResponseNo)).ToListAsync(ct))
            // DateTimeOffset ordering in memory: SQLite cannot ORDER BY it.
            .OrderBy(b => b.SubmittedAt).ThenBy(b => b.ResponseNo).ToList();
        var q = def.Questions[0];
        var known = BallotFilter.For(q);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < q.Options.Count; i++) index[q.Options[i].Key] = i;
        PollResults summary;
        var cells = new List<int[]>(rows.Count);
        switch (q.Type)
        {
            case PollType.Ranked:
            {
                var ballots = rows.Select(r => PollJson.ReadRanked(r.PayloadJson, q, known)).ToList();
                summary = Ranked(def, ballots);
                foreach (var b in ballots)
                {
                    var c = new int[q.Options.Count];
                    foreach (var k in b.Banned) c[index[k]] = -1;
                    for (var r = 0; r < b.Ranked.Count; r++) c[index[b.Ranked[r]]] = r + 1;
                    cells.Add(c);
                }
                break;
            }
            case PollType.Points:
            {
                var ballots = rows.Select(r => PollJson.ReadPoints(r.PayloadJson, q, known)).ToList();
                summary = Points(def, ballots);
                foreach (var b in ballots)
                {
                    var c = new int[q.Options.Count];
                    foreach (var (k, v) in b.Points) c[index[k]] = v;
                    cells.Add(c);
                }
                break;
            }
            case PollType.Dates:
            {
                var ballots = rows.Select(r => PollJson.ReadDates(r.PayloadJson, q, known)).ToList();
                var set = DatesResults.Compute(q, ballots);
                summary = new PollResults(set.VoterCount, null, null) { Dates = set };
                foreach (var b in ballots)
                {
                    var c = new int[q.Options.Count];
                    foreach (var (k, v) in b.Answers) c[index[k]] = (int)v;
                    cells.Add(c);
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(def), q.Type, null);
        }
        var responses = rows.Select((r, i) => new ResponseCells(r.DisplayName, r.Alias, cells[i])).ToList();
        var bytes = 4096L + q.Options.Count * 256L
                    + responses.Sum(r => 96L + r.Cells.Length * 4L + 2L * ((r.DisplayName?.Length ?? 0) + (r.Alias?.Length ?? 0)));
        return new PollResultsData(definitionJson, summary, responses) { EstimatedBytes = bytes };
    }

    private sealed record Row(string PayloadJson, string? DisplayName, string? Alias, DateTimeOffset SubmittedAt, int ResponseNo);

    private static string VoterName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? DatesGridRow.AnonymousName : displayName;

    private static List<DatesGridRow> Grid(IEnumerable<ResponseCells> rows, Func<ResponseCells, string> name) =>
        rows.Select(r => new DatesGridRow(name(r), r.Cells.Select(c => (Availability)c).ToList())).ToList();

    /// <summary>Results of ballots already in hand (the home page examples, DESIGN.md §23.2).</summary>
    public static PollResults Ranked(PollDefinition def, IEnumerable<RankedBallot> ballots)
    {
        var set = RankedResults.Compute(def.Questions[0], ballots);
        return new PollResults(set.VoterCount, set, null);
    }

    /// <inheritdoc cref="Ranked(PollDefinition, IEnumerable{RankedBallot})"/>
    public static PollResults Points(PollDefinition def, IReadOnlyList<PointsBallot> ballots)
    {
        var set = PointsResults.Compute(def.Questions[0], ballots);
        var cap = PointsBuckets.Cap(def);
        var distribution = def.Questions[0].Options.ToDictionary(o => o.Key,
            o => PointsBuckets.FromHistogram(PointsResults.Histogram(ballots, o.Key), cap), StringComparer.Ordinal);
        return new PollResults(set.VoterCount, null, set, distribution);
    }
}
