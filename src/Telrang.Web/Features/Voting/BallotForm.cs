using System.Globalization;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Microsoft.Extensions.Primitives;

namespace Telrang.Web.Features.Voting;

/// <summary>
/// One question's fields in the vote form (DESIGN.md §21.1, §34.4) and the values its editor renders: a saved answer, the
/// carry field's, or what was posted (re-rendered as typed after a refusal, so nothing is lost). Keys are the definition's
/// option keys; unknown keys are ignored, missing ones mean "not ranked" / 0 / no / not picked. <see cref="BallotService"/>
/// validation stays the authority. In a poll with several questions or pages every field name starts with
/// <see cref="Prefix"/> (the question key and a dot: <c>q2.o.paris</c>); a simple poll has none, so its form is as before.
/// </summary>
public sealed class BallotForm
{
    public const string OptionPrefix = "o.", BanPrefix = "b.";
    /// <summary>Choice: the picked option keys (radio group, checkboxes or select), <see cref="OtherValue"/> for Other.</summary>
    public const string ChoiceField = "c", OtherTextField = "other", TextField = "text", NumberField = "number";
    /// <summary>The value of the Other choice; no option key can be it (slugs have no '_', edit keys are '_' + digits).</summary>
    public const string OtherValue = "_other";
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
    /// <summary>Choice: the picked option keys.</summary>
    public HashSet<string> Picked { get; } = new(StringComparer.Ordinal);
    public bool OtherPicked { get; set; }
    public string? OtherText { get; set; }
    /// <summary>Text: the answer as typed.</summary>
    public string? Text { get; set; }
    /// <summary>Number: the value as typed.</summary>
    public string? NumberText { get; set; }
    public string? Name { get; set; }
    public bool Notify { get; set; }
    public DatesView View { get; set; } = DatesView.Calendar;

    /// <summary>Starts every field name of this question (empty in a simple poll).</summary>
    public string Prefix { get; init; } = "";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Field(string key) => OptionPrefix + key;
    public static string BanField(string key) => BanPrefix + key;

    /// <summary>The field prefix of a question: none in a simple poll (one question, no pages or groups), else its key and a dot.</summary>
    public static string PrefixFor(PollDefinition def, PollQuestion q) => VoteLayout.IsSimple(def) ? "" : q.Key + ".";

    public string F(string key) => Prefix + OptionPrefix + key;
    public string BanF(string key) => Prefix + BanPrefix + key;
    public string FieldName(string field) => Prefix + field;
    /// <summary>Element ids: the prefix with the dot as a dash, so ids stay unique per question and need no escaping.</summary>
    public string Id(string id) => Prefix.Length == 0 ? id : Prefix.Replace('.', '-') + id;
    public string ViewName => Prefix + ViewField;
    public string ShownName => Prefix + ShownViewField;

    /// <summary>The editor's start values: the saved response (its first question), or empty.</summary>
    public static BallotForm FromBallot(PollQuestion q, MyBallot? ballot)
    {
        var f = FromAnswer(q, ballot?.Answers.GetValueOrDefault(q.Key) ?? Legacy(ballot), "");
        f.Name = ballot?.DisplayName;
        f.Notify = ballot?.NotifyOnClose ?? false;
        return f;
    }

    private static QuestionAnswer? Legacy(MyBallot? b) =>
        b is null ? null : new QuestionAnswer { Ranked = b.Ranked, Points = b.Points, Dates = b.Dates };

    /// <summary>One question's start values from an answer (stored, or from the carry field).</summary>
    public static BallotForm FromAnswer(PollQuestion q, QuestionAnswer? answer, string prefix)
    {
        var f = new BallotForm { Prefix = prefix };
        var keys = q.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        switch (q.Type)
        {
            case PollType.Ranked:
                var ranked = answer?.Ranked?.Ranked.Where(keys.Contains).Distinct().ToList() ?? [];
                for (var i = 0; i < ranked.Count; i++) f.Positions[ranked[i]] = (i + 1).ToString(Inv);
                foreach (var k in answer?.Ranked?.Banned ?? []) if (keys.Contains(k) && !f.Positions.ContainsKey(k)) f.Banned.Add(k);
                break;
            case PollType.Points:
                foreach (var (k, v) in answer?.Points?.Points ?? new Dictionary<string, int>())
                    if (keys.Contains(k) && v > 0) f.Points[k] = v.ToString(Inv);
                break;
            case PollType.Dates:
                foreach (var (k, a) in answer?.Dates?.Answers ?? new Dictionary<string, Availability>())
                    if (keys.Contains(k) && (a != Availability.Maybe || q.Maybe)) f.Answers[k] = a;
                break;
            case PollType.Choice:
                foreach (var k in answer?.Choice?.Keys ?? []) if (keys.Contains(k)) f.Picked.Add(k);
                f.OtherPicked = q.AllowOther && answer?.Choice?.Other == true;
                f.OtherText = q.AllowOther ? answer?.OtherText : null;
                break;
            case PollType.Text:
                f.Text = answer?.Text;
                break;
            case PollType.Number:
                f.NumberText = answer?.Number?.ToInvariantString();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
        }
        return f;
    }

    /// <summary>What was posted for a simple poll's question, as typed, with the name and notify fields. Only the definition's keys are read.</summary>
    public static BallotForm FromForm(PollQuestion q, IFormCollection form, int maxNameLength)
    {
        var f = FromForm(q, form, "");
        var name = Value(form, NameField);
        // Re-rendered as typed, but never more than the field could hold (BallotService refuses longer names).
        f.Name = name is null ? null : name.Length > maxNameLength * 2 ? name[..(maxNameLength * 2)] : name;
        f.Notify = Value(form, NotifyField) is "1" or "on" or "true";
        return f;
    }

    /// <summary>What was posted for one question (fields named with <paramref name="prefix"/>), as typed.</summary>
    public static BallotForm FromForm(PollQuestion q, IFormCollection form, string prefix)
    {
        var f = new BallotForm { Prefix = prefix };
        f.View = ParseView(Value(form, f.ViewName)) ?? ParseView(Value(form, f.ShownName)) ?? DatesView.Calendar;
        switch (q.Type)
        {
            case PollType.Choice:
            {
                var keys = q.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
                if (form.TryGetValue(f.FieldName(ChoiceField), out var picked))
                {
                    foreach (var v in picked)
                    {
                        if (v == OtherValue) f.OtherPicked = q.AllowOther;
                        else if (v is not null && keys.Contains(v)) f.Picked.Add(v);
                    }
                }
                if (q.AllowOther) f.OtherText = ClipText(Value(form, f.FieldName(OtherTextField)), null);
                return f;
            }
            case PollType.Text:
                f.Text = ClipText(Value(form, f.FieldName(TextField)), q.MaxLength);
                return f;
            case PollType.Number:
                f.NumberText = Value(form, f.FieldName(NumberField)) is { } n ? Clip(n.Trim(), NumberBallotMaxText) : null;
                return f;
        }
        foreach (var o in q.Options)
        {
            var raw = Value(form, f.F(o.Key))?.Trim();
            switch (q.Type)
            {
                case PollType.Ranked:
                    if (!string.IsNullOrEmpty(raw)) f.Positions[o.Key] = Clip(raw);
                    if (q.MaxBans > 0 && Value(form, f.BanF(o.Key)) is { Length: > 0 }) f.Banned.Add(o.Key);
                    break;
                case PollType.Points:
                    if (!string.IsNullOrEmpty(raw)) f.Points[o.Key] = Clip(raw);
                    break;
                case PollType.Dates:
                    var a = raw switch { "yes" => Availability.Yes, "maybe" => Availability.Maybe, _ => Availability.No };
                    if (a != Availability.No) f.Answers[o.Key] = a;
                    break;
                case PollType.Choice:
                case PollType.Text:
                case PollType.Number:
                    break; // read above
                default:
                    throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
            }
        }
        return f;
    }

    /// <summary>Longest number text kept for a re-render (<see cref="NumberBallot.TryParse"/> reads at most 64 characters).</summary>
    private const int NumberBallotMaxText = 64;

    /// <summary>Free text re-rendered as typed, but never far more than the field may hold (the validator refuses longer text).</summary>
    private static string? ClipText(string? text, int? max)
    {
        if (text is null) return null;
        var cap = 2 * Math.Max(max ?? 0, PollLimits.Default.TextAnswerMax) + 16;
        return text.Length > cap ? text[..cap] : text;
    }

    /// <summary>True when a dates layout switch was pressed (no JS): re-render, save nothing.</summary>
    public static bool IsViewSwitch(IFormCollection form) => form.Keys.Any(k => (k == ViewField || k.EndsWith("." + ViewField, StringComparison.Ordinal))
                                                                              && ParseView(Value(form, k)) is not null);

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

    private static string Clip(string s, int max = 12) => s.Length > max ? s[..max] : s;

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

    public const string NumberFormatMessage = "Enter a number such as 12.5.";

    /// <summary>
    /// The posted fields as an answer of <paramref name="q"/>'s type; null with <paramref name="errors"/> when they cannot
    /// become one (ranked positions, points or a number that are no numbers). Rules are the validator's (<see cref="ResponseAnswers.Validate"/>).
    /// </summary>
    public QuestionAnswer? ToAnswer(PollQuestion q, List<string> errors)
    {
        switch (q.Type)
        {
            case PollType.Ranked:
                return ToRanked(q, errors) is { } r ? new QuestionAnswer { Ranked = r } : null;
            case PollType.Points:
                return ToPoints(q, errors) is { } p ? new QuestionAnswer { Points = p } : null;
            case PollType.Dates:
                return new QuestionAnswer { Dates = ToDates() };
            case PollType.Choice:
                return new QuestionAnswer
                {
                    Choice = new ChoiceBallot(q.Options.Select(o => o.Key).Where(Picked.Contains).ToList(), OtherPicked),
                    OtherText = string.IsNullOrWhiteSpace(OtherText) ? null : OtherText.Trim(),
                };
            case PollType.Text:
                return new QuestionAnswer { Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim() };
            case PollType.Number:
            {
                var text = NumberText?.Trim();
                if (string.IsNullOrEmpty(text)) return new QuestionAnswer();
                if (text.Length > NumberBallotMaxText || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var v))
                {
                    Add(errors, NumberFormatMessage);
                    return null;
                }
                return new QuestionAnswer { Number = new NumberBallot(v) };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
        }
    }

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
