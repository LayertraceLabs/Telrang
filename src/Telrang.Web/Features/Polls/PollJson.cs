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
    public static string Serialize(string questionKey, ChoiceBallot ballot) => new JsonObject { [questionKey] = ChoiceNode(ballot) }.ToJsonString(Options);
    public static string Serialize(string questionKey, NumberBallot ballot) => new JsonObject { [questionKey] = NumberNode(ballot) }.ToJsonString(Options);

    private static string Part<T>(string questionKey, T ballot) =>
        new JsonObject { [questionKey] = JsonSerializer.SerializeToNode(ballot, Options) }.ToJsonString(Options);

    /// <summary>
    /// A payload of several questions (DESIGN.md §34.3): <c>{"q1": {...}, "q2": {...}}</c>, one part per answered question in
    /// the order given. A question without a part is not answered.
    /// </summary>
    public static string SerializeParts(IEnumerable<KeyValuePair<string, JsonNode>> parts)
    {
        var o = new JsonObject();
        foreach (var (key, node) in parts) o[key] = node;
        return o.ToJsonString(Options);
    }

    public static JsonNode RankedNode(RankedBallot b) => JsonSerializer.SerializeToNode(b, Options)!;
    public static JsonNode PointsNode(PointsBallot b) => JsonSerializer.SerializeToNode(b, Options)!;
    public static JsonNode DatesNode(DatesBallot b) => JsonSerializer.SerializeToNode(b, Options)!;

    /// <summary>Choice: <c>{"keys": [...]}</c>, plus <c>"other": true</c> when Other was picked (its text is stored apart).</summary>
    public static JsonNode ChoiceNode(ChoiceBallot b)
    {
        var o = new JsonObject { ["keys"] = new JsonArray(b.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()) };
        if (b.Other) o["other"] = true;
        return o;
    }

    /// <summary>Number: <c>{"value": "12.5"}</c>, the decimal as an invariant string (never a JSON number, so nothing rounds it).</summary>
    public static JsonNode NumberNode(NumberBallot b) => new JsonObject { ["value"] = b.ToInvariantString() };

    /// <summary>Text: the part says "answered" and nothing else; the text itself is free text, stored apart (<c>Ballot.TextCipher</c>).</summary>
    public static JsonNode TextNode() => new JsonObject();

    /// <summary>The question has a part in the payload (answered; for a text question the only thing the payload says).</summary>
    public static bool HasPart(string json, string questionKey)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(questionKey, out var part)
               && part.ValueKind == JsonValueKind.Object;
    }

    /// <summary>A choice part: the keys (strings only) and the Other flag; a missing part is an empty ballot.</summary>
    public static ChoiceBallot DeserializeChoice(string json, string questionKey)
    {
        using var doc = JsonDocument.Parse(json);
        return TryPart(doc.RootElement, questionKey, out var part) ? ChoiceFrom(part) : new ChoiceBallot([]);
    }

    /// <summary>A choice part already found (<see cref="TryPart"/>): the keys (strings only) and the Other flag.</summary>
    public static ChoiceBallot ChoiceFrom(JsonElement part)
    {
        var keys = new List<string>();
        if (part.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array)
            foreach (var e in k.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) keys.Add(e.GetString()!);
        var other = part.TryGetProperty("other", out var x) && x.ValueKind == JsonValueKind.True;
        return new ChoiceBallot(keys, other);
    }

    /// <summary>A number part, or null when missing or not a decimal string (read invariant, never rounded).</summary>
    public static NumberBallot? DeserializeNumber(string json, string questionKey)
    {
        using var doc = JsonDocument.Parse(json);
        return TryPart(doc.RootElement, questionKey, out var part) ? NumberFrom(part) : null;
    }

    /// <summary>A number part already found (<see cref="TryPart"/>), or null when it holds no decimal string.</summary>
    public static NumberBallot? NumberFrom(JsonElement part)
    {
        if (!part.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) return null;
        return decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? new NumberBallot(d) : null;
    }

    public static RankedBallot DeserializeRanked(string json, string questionKey) =>
        Read<RankedBallot>(json, questionKey) ?? new RankedBallot([], []);

    public static PointsBallot DeserializePoints(string json, string questionKey) =>
        Read<PointsBallot>(json, questionKey) ?? new PointsBallot(new Dictionary<string, int>());

    public static DatesBallot DeserializeDates(string json, string questionKey) =>
        Read<DatesBallot>(json, questionKey) ?? new DatesBallot(new Dictionary<string, Availability>());

    private static T? Read<T>(string json, string questionKey) where T : class
    {
        using var doc = JsonDocument.Parse(json);
        return TryPart(doc.RootElement, questionKey, out var part) ? part.Deserialize<T>(Options) : null;
    }

    // ---- one parse per payload, every question (DESIGN.md §34.5: results score all questions in one pass) ----

    /// <summary>The part of <paramref name="questionKey"/> in a parsed payload: present and an object (answered).</summary>
    public static bool TryPart(JsonElement root, string questionKey, out JsonElement part)
    {
        part = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(questionKey, out part) && part.ValueKind == JsonValueKind.Object;
    }

    public static RankedBallot RankedFrom(JsonElement part) => part.Deserialize<RankedBallot>(Options) ?? new RankedBallot([], []);

    public static PointsBallot PointsFrom(JsonElement part) => part.Deserialize<PointsBallot>(Options) ?? new PointsBallot(new Dictionary<string, int>());

    public static DatesBallot DatesFrom(JsonElement part) => part.Deserialize<DatesBallot>(Options) ?? new DatesBallot(new Dictionary<string, Availability>());

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

    /// <summary>A choice part with removed options dropped; the Other flag only while the question offers Other.</summary>
    public static ChoiceBallot ReadChoice(string json, PollQuestion q, KnownKeys? known = null)
    {
        var b = (known ?? BallotFilter.For(q)).Filter(DeserializeChoice(json, q.Key));
        return b.Other && !q.AllowOther ? b with { Other = false } : b;
    }

    public static NumberBallot? ReadNumber(string json, PollQuestion q) => DeserializeNumber(json, q.Key);

    /// <summary>
    /// Per key of <paramref name="keys"/>: how many stored payloads answered it (ranked or banned, points above 0, yes or
    /// maybe, picked): the ballot counts of removals in the change log and the editor's preview. One parse per payload, whatever
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
                case PollType.Choice:
                    foreach (var k in DeserializeChoice(json, q.Key).Keys) seen.Add(k);
                    break;
                case PollType.Text:
                case PollType.Number:
                    break; // no options
                default:
                    throw new ArgumentOutOfRangeException(nameof(q), q.Type, null);
            }
            foreach (var k in seen) if (counts.TryGetValue(k, out var n)) counts[k] = n + 1;
        }
        return counts;
    }
}
