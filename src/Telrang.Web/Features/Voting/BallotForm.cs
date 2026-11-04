using System.Globalization;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Microsoft.Extensions.Primitives;

namespace Telrang.Web.Features.Voting;

/// <summary>
/// The vote form's fields (DESIGN.md §21.1) and the values a ballot editor renders: a saved ballot, or what was posted
/// (re-rendered as typed after a refusal, so nothing is lost). Keys are the definition's option keys; unknown keys are
/// ignored, missing ones mean "not ranked" / 0 / no. <see cref="BallotService"/> validation stays the authority.
/// </summary>
public sealed class BallotForm
{
    public const string OptionPrefix = "o.", BanPrefix = "b.";
    public const string NameField = "name", NotifyField = "notify", FormIdField = "fid", ResponseField = "r";
    /// <summary>The no-JS layout switch of a dates ballot: a submit button that re-renders and saves nothing.</summary>
    public const string ViewField = "view";
    /// <summary>The layout shown when the form was sent (dates.js keeps it current), so a re-render keeps it.</summary>
    public const string ShownViewField = "shown";
    public const string ConfirmField = "confirm";
    public const string TurnstileField = "cf-turnstile-response", HCaptchaField = "h-captcha-response";

    public const string TiesMessage = "Give each ranked option its own position.";
    public const string PositionFormatMessage = "Enter positions as whole numbers from 1.";
    public const string RankedAndBannedMessage = "Rank or ban an option, not both.";
    public const string PointsFormatMessage = "Enter points as whole numbers from 0.";

    /// <summary>Ranked: the position field per option as typed (empty = not ranked).</summary>
    public Dictionary<string, string> Positions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Banned { get; } = new(StringComparer.Ordinal);
    /// <summary>Points: the field per option as typed (empty = 0).</summary>
    public Dictionary<string, string> Points { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Availability> Answers { get; } = new(StringComparer.Ordinal);
    public string? Name { get; set; }
    public bool Notify { get; set; }
    public DatesView View { get; set; } = DatesView.Calendar;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Field(string key) => OptionPrefix + key;
    public static string BanField(string key) => BanPrefix + key;

    /// <summary>The editor's start values: the saved response, or empty.</summary>
    public static BallotForm FromBallot(PollQuestion q, MyBallot? ballot)
    {
        var f = new BallotForm { Name = ballot?.DisplayName, Notify = ballot?.NotifyOnClose ?? false };
        var keys = q.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        switch (q.Type)
        {
            case PollType.Ranked:
                var ranked = ballot?.Ranked?.Ranked.Where(keys.Contains).Distinct().ToList() ?? [];
                for (var i = 0; i < ranked.Count; i++) f.Positions[ranked[i]] = (i + 1).ToString(Inv);
                foreach (var k in ballot?.Ranked?.Banned ?? []) if (keys.Contains(k) && !f.Positions.ContainsKey(k)) f.Banned.Add(k);
                break;
            case PollType.Points:
                foreach (var (k, v) in ballot?.Points?.Points ?? new Dictionary<string, int>())
                    if (keys.Contains(k) && v > 0) f.Points[k] = v.ToString(Inv);
                break;
            case PollType.Dates:
                foreach (var (k, a) in ballot?.Dates?.Answers ?? new Dictionary<string, Availability>())
                    if (keys.Contains(k) && (a != Availability.Maybe || q.Maybe)) f.Answers[k] = a;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
        }
        return f;
    }

    /// <summary>What was posted, as typed. Only the definition's keys are read.</summary>
    public static BallotForm FromForm(PollQuestion q, IFormCollection form, int maxNameLength)
    {
        var name = Value(form, NameField);
        var f = new BallotForm
        {
            // Re-rendered as typed, but never more than the field could hold (BallotService refuses longer names).
            Name = name is null ? null : name.Length > maxNameLength * 2 ? name[..(maxNameLength * 2)] : name,
            Notify = Value(form, NotifyField) is "1" or "on" or "true",
            View = ParseView(Value(form, ViewField)) ?? ParseView(Value(form, ShownViewField)) ?? DatesView.Calendar,
        };
        foreach (var o in q.Options)
        {
            var raw = Value(form, Field(o.Key))?.Trim();
            switch (q.Type)
            {
                case PollType.Ranked:
                    if (!string.IsNullOrEmpty(raw)) f.Positions[o.Key] = Clip(raw);
                    if (q.MaxBans > 0 && Value(form, BanField(o.Key)) is { Length: > 0 }) f.Banned.Add(o.Key);
                    break;
                case PollType.Points:
                    if (!string.IsNullOrEmpty(raw)) f.Points[o.Key] = Clip(raw);
                    break;
                case PollType.Dates:
                    var a = raw switch { "yes" => Availability.Yes, "maybe" => Availability.Maybe, _ => Availability.No };
                    if (a != Availability.No) f.Answers[o.Key] = a;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
            }
        }
        return f;
    }

    /// <summary>True when the dates layout switch was pressed (no JS): re-render, save nothing.</summary>
    public static bool IsViewSwitch(IFormCollection form) => ParseView(Value(form, ViewField)) is not null;

    public static DatesView? ParseView(string? value) => value switch
    {
        "calendar" => DatesView.Calendar,
        "list" => DatesView.List,
        "timeline" => DatesView.Timeline,
        _ => null,
    };

    public static string ViewValue(DatesView v) => v.ToString().ToLowerInvariant();

    /// <summary>A positive response number from the form, or null.</summary>
    public static int? ResponseNo(IFormCollection form) =>
        int.TryParse(Value(form, ResponseField), NumberStyles.None, Inv, out var n) && n is > 0 and < 1_000_000 ? n : null;

    public static string? Value(IFormCollection form, string key) =>
        form.TryGetValue(key, out StringValues v) && v.Count > 0 ? v[0] : null;

    public static string? CaptchaToken(IFormCollection form) =>
        Value(form, TurnstileField) is { Length: > 0 } t ? t : Value(form, HCaptchaField);

    private static string Clip(string s) => s.Length > 12 ? s[..12] : s;

    // ---- to ballots --------------------------------------------------------------------------------

    /// <summary>
    /// Positions to a ranking: ties are refused, gaps compacted (2, 5, 9 → 1, 2, 3), ranked and banned is refused.
    /// Null with errors when the fields cannot become a ballot.
    /// </summary>
    public RankedBallot? ToRanked(PollQuestion q, List<string> errors)
    {
        var positions = new List<(int Pos, int Order, string Key)>();
        var order = 0;
        foreach (var o in q.Options)
        {
            order++;
            if (!Positions.TryGetValue(o.Key, out var raw)) continue;
            if (!int.TryParse(raw, NumberStyles.None, Inv, out var p) || p < 1) { Add(errors, PositionFormatMessage); continue; }
            positions.Add((p, order, o.Key));
        }
        if (positions.GroupBy(p => p.Pos).Any(g => g.Count() > 1)) Add(errors, TiesMessage);
        if (positions.Any(p => Banned.Contains(p.Key))) Add(errors, RankedAndBannedMessage);
        if (errors.Count > 0) return null;
        var ranked = positions.OrderBy(p => p.Pos).Select(p => p.Key).ToList();
        var banned = q.Options.Select(o => o.Key).Where(Banned.Contains).ToList();
        return new RankedBallot(ranked, banned);
    }

    public PointsBallot? ToPoints(PollQuestion q, List<string> errors)
    {
        var points = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in q.Options)
        {
            if (!Points.TryGetValue(o.Key, out var raw)) continue;
            if (!int.TryParse(raw, NumberStyles.None, Inv, out var v)) { Add(errors, PointsFormatMessage); continue; }
            if (v > 0) points[o.Key] = v;
        }
        return errors.Count > 0 ? null : new PointsBallot(points);
    }

    public DatesBallot ToDates() => new(new Dictionary<string, Availability>(Answers, StringComparer.Ordinal));

    /// <summary>The ranked order the drag zones show: by position (ties in option order), then the unranked options.</summary>
    public List<string> RankedOrder(PollQuestion q)
    {
        var order = q.Options.Select((o, i) => (o.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        return Positions
            .Select(p => (p.Key, Pos: int.TryParse(p.Value, NumberStyles.None, Inv, out var n) && n > 0 ? n : int.MaxValue))
            .Where(p => p.Pos != int.MaxValue && order.ContainsKey(p.Key))
            .OrderBy(p => p.Pos).ThenBy(p => order[p.Key])
            .Select(p => p.Key).ToList();
    }

    public int PointsOf(string key) =>
        Points.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.None, Inv, out var v) ? v : 0;

    public Availability AnswerOf(string key) => Answers.GetValueOrDefault(key, Availability.No);

    private static void Add(List<string> errors, string message)
    {
        if (!errors.Contains(message)) errors.Add(message);
    }
}
