using System.Globalization;
using System.Text;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Results;

/// <summary>RFC 4180 CSV writing: CRLF line ends, fields quoted when needed, quotes doubled.</summary>
public static class Csv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A user-controlled text cell. Besides RFC 4180 quoting, a leading = + - @ (or tab/CR) gets a
    /// leading apostrophe so spreadsheet apps do not evaluate it as a formula (CSV injection).
    /// </summary>
    public static string Text(string? value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return Quote(value);
    }

    /// <summary>A cell the app produced itself (numbers, dates, fixed words): quoted only if needed.</summary>
    public static string Raw(string value) => Quote(value);

    public static string Number(long value) => value.ToString(Inv);

    public static string Number(double value, string format) => value.ToString(format, Inv);

    public static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    public static void Line(StringBuilder sb, IEnumerable<string> cells) => sb.Append(string.Join(',', cells)).Append("\r\n");

    /// <summary>UTF-8 with BOM (Excel needs it to detect UTF-8).</summary>
    public static byte[] ToBytes(StringBuilder sb) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];

    /// <summary>ASCII letters, digits and dashes from the title; "poll" when nothing is left.</summary>
    public static string SafeFileName(string title, string suffix)
    {
        var sb = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') sb.Append(c);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 60) break;
        }
        var name = sb.ToString().Trim('-');
        return (name.Length == 0 ? "poll" : name) + "-" + suffix + ".csv";
    }
}

/// <summary>Owner-only CSV downloads: raw ballots and computed results.</summary>
public sealed class CsvExportService(IDbContextFactory<ApplicationDbContext> dbFactory, PollAccessService access, ResultsService results,
    ResponseTextProtection textProtection)
{
    public sealed record CsvFile(string FileName, byte[] Content);

    /// <summary>Null when the poll is unknown or not owned by <paramref name="userId"/>.</summary>
    public async Task<CsvFile?> BallotsAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return null;
        var def = a.Definition;
        if (ResultsService.UsesResponseTable(def)) return await ResponsesAsync(db, a, ct);
        var rows = (await db.Ballots.AsNoTracking().Where(b => b.PollId == a.Poll.Id)
                .Select(b => new { b.DisplayName, b.Alias, b.SubmittedAt, b.UpdatedAt, b.PayloadJson }).ToListAsync(ct))
            .OrderBy(b => b.SubmittedAt).ToList();

        var sb = new StringBuilder();
        // Name = what the voter entered, Alias = the owner's name for the response (DESIGN.md §20.3).
        var known = BallotFilter.For(def.Questions[0]);
        Csv.Line(sb, new[] { "Name", "Alias", "SubmittedAt", "UpdatedAt" }.Select(Csv.Raw).Concat(def.Questions[0].Options.Select(o => Csv.Text(o.Label))));
        foreach (var row in rows)
        {
            IEnumerable<string> cells;
            switch (def.Questions[0].Type)
            {
                case PollType.Ranked:
                {
                    var ballot = PollJson.ReadRanked(row.PayloadJson, def.Questions[0], known);
                    cells = def.Questions[0].Options.Select(o =>
                    {
                        var rank = IndexOf(ballot.Ranked, o.Key);
                        return rank >= 0 ? Csv.Number(rank + 1) : ballot.Banned.Contains(o.Key) ? "BAN" : "";
                    });
                    break;
                }
                case PollType.Points:
                {
                    var ballot = PollJson.ReadPoints(row.PayloadJson, def.Questions[0], known);
                    cells = def.Questions[0].Options.Select(o => Csv.Number(ballot.Points.GetValueOrDefault(o.Key)));
                    break;
                }
                case PollType.Dates:
                {
                    var ballot = PollJson.ReadDates(row.PayloadJson, def.Questions[0], known);
                    cells = def.Questions[0].Options.Select(o => AvailabilityWord(ballot.Answers.GetValueOrDefault(o.Key, Availability.No)));
                    break;
                }
                case PollType.Choice:
                case PollType.Text:
                case PollType.Number:
                    throw new System.Diagnostics.UnreachableException("UsesResponseTable covers these types.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(encodedId), def.Questions[0].Type, null);
            }
            Csv.Line(sb, new[] { Csv.Text(row.DisplayName), Csv.Text(row.Alias), Csv.Timestamp(row.SubmittedAt), Csv.Timestamp(row.UpdatedAt) }.Concat(cells));
        }
        return new CsvFile(Csv.SafeFileName(def.Title, "ballots"), Csv.ToBytes(sb));
    }

    /// <summary>Responses read and decrypted per chunk, so a large poll's free text is never all in memory at once.</summary>
    public const int ExportChunk = 500;

    /// <summary>
    /// The ballots CSV of a structured poll (DESIGN.md §34.5): one row per response in submission order, one column per question
    /// (<c>q2 · Diet</c>), each cell <see cref="AnswerText"/>'s form, free text included (the owner's file). Every cell but a
    /// number goes through <see cref="Csv.Text"/>, so no label or text starts a formula.
    /// </summary>
    private async Task<CsvFile> ResponsesAsync(ApplicationDbContext db, PollAccess a, CancellationToken ct)
    {
        var def = a.Definition;
        var data = await results.OwnerDataAsync(a, ct);
        var free = ResultsService.HasFreeText(def);
        var sb = new StringBuilder();
        Csv.Line(sb, new[] { "Name", "Alias", "SubmittedAt", "UpdatedAt" }.Select(Csv.Raw)
            .Concat(def.Questions.Select(q => Csv.Text(q.Key + " · " + BallotService.QuestionLabel(def, q)))));
        foreach (var chunk in data.Responses.Chunk(ExportChunk))
        {
            var ids = chunk.Select(r => r.BallotId).ToList();
            var stored = (await db.Ballots.AsNoTracking().Where(b => b.PollId == a.Poll.Id && ids.Contains(b.Id))
                    .Select(b => new { b.Id, b.SubmittedAt, b.UpdatedAt, b.TextCipher }).ToListAsync(ct))
                .ToDictionary(b => b.Id);
            foreach (var r in chunk)
            {
                if (!stored.TryGetValue(r.BallotId, out var row)) continue; // deleted since the results were computed
                var texts = free ? textProtection.RevealTexts(a.Poll.Id, row.TextCipher) : null;
                var cells = def.Questions.Select((q, i) =>
                {
                    var text = AnswerText.Cell(q, data.Layout, i, r, owner: true, texts, AnswerText.Form.Csv);
                    return q.Type == PollType.Number ? Csv.Raw(text) : Csv.Text(text);
                });
                Csv.Line(sb, new[] { Csv.Text(r.DisplayName), Csv.Text(r.Alias), Csv.Timestamp(row.SubmittedAt), Csv.Timestamp(row.UpdatedAt) }.Concat(cells));
            }
        }
        return new CsvFile(Csv.SafeFileName(def.Title, "ballots"), Csv.ToBytes(sb));
    }

    public async Task<CsvFile?> ResultsAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        var a = await access.GetAsync(encodedId, VisitorContext.ForUser(userId), ct);
        if (a is null || !a.CanEdit) return null;
        var r = await results.ComputeAsync(a, ct);
        var def = a.Definition;
        var sb = new StringBuilder();
        if (ResultsService.UsesResponseTable(def)) QuestionsTable(sb, def, r);
        else if (r.Ranked is { } ranked)
        {
            Csv.Line(sb, ["Place", "Option", "Score", "FirstChoices", "Bans", "BanPercent"]);
            foreach (var (row, place) in Places(ranked.Rows, x => Math.Round(x.Score, 9)))
            {
                Csv.Line(sb, [Csv.Number(place), Csv.Text(row.Label), Csv.Number(row.Score, def.Questions[0].Scoring == ScoringScheme.Borda ? "0" : "0.####"),
                    Csv.Number(row.FirstChoiceCount), Csv.Number(row.BanCount), Csv.Number(Pct(row.BanCount, ranked.VoterCount), "0.#")]);
            }
        }
        else if (r.Dates is { } dates)
        {
            Csv.Line(sb, ["Place", "Date", "Yes", "Maybe", "No"]);
            foreach (var row in dates.Rows)
                Csv.Line(sb, [Csv.Number(row.Place), Csv.Text(row.Label), Csv.Number(row.Yes), Csv.Number(row.Maybe), Csv.Number(row.No)]);
        }
        else if (r.Points is { } points)
        {
            var cap = PointsBuckets.Cap(def);
            Csv.Line(sb, new[] { "Place", "Option", "Total", "Mean", "Supporters", "SupporterPercent", "Max" }
                .Concat(Enumerable.Range(0, PointsBuckets.Count).Select(k => Csv.Raw("Voters " + PointsBuckets.Label(k, cap) + " pts"))));
            foreach (var (row, place) in Places(points.Rows, x => (double)x.Total))
            {
                var buckets = r.PointsDistribution?.GetValueOrDefault(row.Key) ?? new int[PointsBuckets.Count];
                Csv.Line(sb, new[] { Csv.Number(place), Csv.Text(row.Label), Csv.Number(row.Total), Csv.Number(row.Mean, "0.##"),
                    Csv.Number(row.SupporterCount), Csv.Number(Pct(row.SupporterCount, points.VoterCount), "0.#"), Csv.Number(row.Max) }
                    .Concat(buckets.Select(c => Csv.Number(c))));
            }
        }
        return new CsvFile(Csv.SafeFileName(def.Title, "results"), Csv.ToBytes(sb));
    }

    /// <summary>
    /// The results CSV of a structured poll (or one choice, text or number question): one row per figure,
    /// <c>Question,Option,Measure,Value</c>, so every type fits one table. Counts only, never free text.
    /// </summary>
    private static void QuestionsTable(StringBuilder sb, PollDefinition def, PollResults r)
    {
        Csv.Line(sb, ["Question", "Option", "Measure", "Value"]);
        foreach (var qr in r.Questions)
        {
            var q = qr.Question;
            var name = Csv.Text(q.Key + " · " + BallotService.QuestionLabel(def, q));
            void Add(string? option, string measure, string value) =>
                Csv.Line(sb, [name, option is null ? "" : Csv.Text(option), Csv.Raw(measure), value]);
            switch (q.Type)
            {
                case PollType.Ranked:
                    foreach (var (row, place) in Places(qr.Ranked!.Rows, x => Math.Round(x.Score, 9)))
                    {
                        Add(row.Label, "Place", Csv.Number(place));
                        Add(row.Label, "Score", Csv.Number(row.Score, q.Scoring == ScoringScheme.Borda ? "0" : "0.####"));
                        Add(row.Label, "FirstChoices", Csv.Number(row.FirstChoiceCount));
                        Add(row.Label, "Bans", Csv.Number(row.BanCount));
                    }
                    break;
                case PollType.Points:
                    foreach (var (row, place) in Places(qr.Points!.Rows, x => (double)x.Total))
                    {
                        Add(row.Label, "Place", Csv.Number(place));
                        Add(row.Label, "Total", Csv.Number(row.Total));
                        Add(row.Label, "Mean", Csv.Number(row.Mean, "0.##"));
                        Add(row.Label, "Supporters", Csv.Number(row.SupporterCount));
                    }
                    break;
                case PollType.Dates:
                    foreach (var row in qr.Dates!.Rows)
                    {
                        Add(row.Label, "Place", Csv.Number(row.Place));
                        Add(row.Label, "Yes", Csv.Number(row.Yes));
                        Add(row.Label, "Maybe", Csv.Number(row.Maybe));
                        Add(row.Label, "No", Csv.Number(row.No));
                    }
                    break;
                case PollType.Choice:
                {
                    var c = qr.Choice!;
                    Add(null, "Respondents", Csv.Number(c.RespondentCount));
                    foreach (var row in c.Rows)
                    {
                        Add(row.Label, "Count", Csv.Number(row.Count));
                        Add(row.Label, "Percent", Csv.Number(row.Share * 100, "0.#"));
                    }
                    if (q.AllowOther)
                    {
                        Add(AnswerText.OtherLabel, "Count", Csv.Number(c.OtherCount));
                        Add(AnswerText.OtherLabel, "Percent", Csv.Number(c.OtherShare * 100, "0.#"));
                    }
                    break;
                }
                case PollType.Number:
                {
                    var n = qr.Number!;
                    Add(null, "Count", Csv.Number(n.Count));
                    foreach (var (measure, value, places) in new[] { ("Min", n.Min, 28), ("Max", n.Max, 28), ("Mean", n.Mean, q.Decimals + 2), ("Median", n.Median, q.Decimals + 1) })
                        Add(null, measure, value is { } v ? Csv.Raw(PollText.Decimal(v, places)) : "");
                    break;
                }
                case PollType.Text:
                    Add(null, "Answers", Csv.Number(qr.Text!.AnswerCount));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(def), q.Type, null);
            }
        }
    }

    private static string AvailabilityWord(Availability a) => a switch
    {
        Availability.Yes => "yes",
        Availability.Maybe => "maybe",
        _ => "no",
    };

    private static int IndexOf(IReadOnlyList<string> list, string key)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == key) return i;
        return -1;
    }

    private static double Pct(int count, int voters) => voters == 0 ? 0 : count * 100.0 / voters;

    /// <summary>Competition ranking (1, 2, 2, 4) over rows already sorted best-first.</summary>
    private static IEnumerable<(T Row, int Place)> Places<T>(IReadOnlyList<T> rows, Func<T, double> score)
    {
        var place = 0;
        double? previous = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var s = score(rows[i]);
            if (previous != s) place = i + 1;
            previous = s;
            yield return (rows[i], place);
        }
    }
}

/// <summary>GET /polls/{id}/export.csv and /polls/{id}/results.csv (signed-in owner only; anyone else 404).</summary>
public static class CsvExportEndpoints
{
    public static IEndpointRouteBuilder MapPollExports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/polls/{id}").RequireAuthorization();
        group.MapGet("/export.csv", (string id, HttpContext http, CsvExportService csv, UserManager<ApplicationUser> users) =>
            Send(http, csv.BallotsAsync(users.GetUserId(http.User)!, id, http.RequestAborted)));
        group.MapGet("/results.csv", (string id, HttpContext http, CsvExportService csv, UserManager<ApplicationUser> users) =>
            Send(http, csv.ResultsAsync(users.GetUserId(http.User)!, id, http.RequestAborted)));
        return app;
    }

    private static async Task<IResult> Send(HttpContext http, Task<CsvExportService.CsvFile?> file)
    {
        if (await file is not { } f) return TypedResults.NotFound();
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(f.Content, "text/csv; charset=utf-8", f.FileName);
    }
}
