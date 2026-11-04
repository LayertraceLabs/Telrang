using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// JSON (de)serialization of the stored definition (<c>Poll.DefinitionJson</c>, the only stored form of a poll) and of
/// ballot payloads. A payload is an object keyed by question key, one part per question: <c>{"q1": {...}}</c>
/// (DESIGN.md §20.1). A missing part reads as an empty ballot for that question.
/// </summary>
public static class PollJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(PollDefinition definition) => JsonSerializer.Serialize(definition, Options);

    /// <summary>
    /// Reads a stored definition. A missing time zone reads as UTC; missing results timing and ballots (stored before
    /// DESIGN.md §25) read as while voting and off; an invite or account poll stored with results for anyone reads as
    /// results for voters (§25: its address alone never shows results; migration <c>ResultsSettings</c> rewrites them).
    /// </summary>
    public static PollDefinition DeserializeDefinition(string json)
    {
        var def = JsonSerializer.Deserialize<PollDefinition>(json, Options)
                  ?? throw new InvalidOperationException("Stored poll definition is empty.");
        if (def.TimeZone is null) def = def with { TimeZone = PollTime.Utc };
        if (def.Results == ResultsVisibility.Open && def.Voters != VoterMode.Public) def = def with { Results = ResultsVisibility.AfterVote };
        return def;
    }

    /// <summary>A draft as JSON (browser drafts, wwwroot/js/draft.js).</summary>
    public static string Serialize(PollDraft draft) => JsonSerializer.Serialize(draft, Options);

    /// <summary>A browser draft, or null when the JSON is not a draft (an old or damaged entry).</summary>
    public static PollDraft? DeserializeDraft(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var d = JsonSerializer.Deserialize<PollDraft>(json, Options);
            if (d is null || d.Questions is null || d.Questions.Count == 0 || d.Questions.Any(q => q is null || q.Options is null)) return null;
            return d;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- ballots: one part per question ----

    public static string Serialize(string questionKey, RankedBallot ballot) => Part(questionKey, ballot);
    public static string Serialize(string questionKey, PointsBallot ballot) => Part(questionKey, ballot);
    public static string Serialize(string questionKey, DatesBallot ballot) => Part(questionKey, ballot);

    private static string Part<T>(string questionKey, T ballot) =>
        new JsonObject { [questionKey] = JsonSerializer.SerializeToNode(ballot, Options) }.ToJsonString(Options);

    public static RankedBallot DeserializeRanked(string json, string questionKey) =>
        Read<RankedBallot>(json, questionKey) ?? new RankedBallot([], []);

    public static PointsBallot DeserializePoints(string json, string questionKey) =>
        Read<PointsBallot>(json, questionKey) ?? new PointsBallot(new Dictionary<string, int>());

    public static DatesBallot DeserializeDates(string json, string questionKey) =>
        Read<DatesBallot>(json, questionKey) ?? new DatesBallot(new Dictionary<string, Availability>());

    private static T? Read<T>(string json, string questionKey) where T : class
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(questionKey, out var part)
               && part.ValueKind == JsonValueKind.Object
            ? part.Deserialize<T>(Options)
            : null;
    }

    // ---- reading a question's part for display or scoring (DESIGN.md §26) ----
    // Ballots stay raw: a stored payload may name options the question no longer has. These readers drop them
    // (BallotFilter), so every page, CSV and summary reads only the question's current options.
    // A reader of many ballots passes one KnownKeys (BallotFilter.For) for all of them.

    public static RankedBallot ReadRanked(string json, PollQuestion q, KnownKeys? known = null) =>
        (known ?? BallotFilter.For(q)).Filter(DeserializeRanked(json, q.Key));

    public static PointsBallot ReadPoints(string json, PollQuestion q, KnownKeys? known = null) =>
        (known ?? BallotFilter.For(q)).Filter(DeserializePoints(json, q.Key));

    public static DatesBallot ReadDates(string json, PollQuestion q, KnownKeys? known = null) =>
        (known ?? BallotFilter.For(q)).Filter(DeserializeDates(json, q.Key));

    /// <summary>
    /// Per key of <paramref name="keys"/>: how many stored payloads answered it (ranked or banned, points above 0, yes or
    /// maybe): the ballot counts of removals in the change log and the editor's preview. One parse per payload, whatever
    /// the number of keys (DESIGN.md §31, pentest R3-VOTE-3 / R3-RES-5).
    /// </summary>
    public static Dictionary<string, int> AnswerCounts(IEnumerable<string> payloads, PollQuestion q, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(q);
        var counts = keys.Distinct(StringComparer.Ordinal).ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        if (counts.Count == 0) return counts;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in payloads)
        {
            seen.Clear();
            switch (q.Type)
            {
                case PollType.Ranked:
                    var r = DeserializeRanked(json, q.Key);
                    foreach (var k in r.Ranked) seen.Add(k);
                    foreach (var k in r.Banned) seen.Add(k);
                    break;
                case PollType.Points:
                    foreach (var (k, v) in DeserializePoints(json, q.Key).Points) if (v > 0) seen.Add(k);
                    break;
                case PollType.Dates:
                    foreach (var (k, v) in DeserializeDates(json, q.Key).Answers) if (v is Availability.Yes or Availability.Maybe) seen.Add(k);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
            }
            foreach (var k in seen) if (counts.TryGetValue(k, out var n)) counts[k] = n + 1;
        }
        return counts;
    }
}
