using Telrang.Core.Polls;
using Telrang.Web.Features.Settings;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// The validator as the web app uses it: Core's <see cref="PollDraftValidator"/> with the admin's limits (§14.4), plus
/// the checks Core cannot do (the time zone against this host's zone database, the owner's max poll duration). Used by
/// the editor's live preview and re-run by <see cref="PollService"/> on save, so both report the same field errors.
/// </summary>
public static class PollParsing
{
    public static PollLimits LimitsFrom(LimitSettings s) => new(
        TitleMax: s.TitleLength,
        OptionLabelMax: s.OptionLabelLength,
        OptionDescriptionMax: s.OptionDescriptionLength,
        DescriptionMax: s.PollDescriptionLength,
        OptionsMax: s.OptionsPerPoll);

    /// <summary>The latest allowed close date for a poll created at <paramref name="createdAt"/> (null = no limit).</summary>
    public static DateOnly? LatestClose(int maxDurationDays, DateTimeOffset createdAt) =>
        maxDurationDays > 0 ? DateOnly.FromDateTime(createdAt.UtcDateTime).AddDays(maxDurationDays) : null;

    /// <param name="maxDurationDays">Max poll duration (0 = none): the close date may be at most this many days after
    /// <paramref name="createdAt"/> (now for a new poll).</param>
    /// <param name="existing">On edit: the stored definition (edit rules; its close date is exempt from the duration limit,
    /// so a transferred poll keeps the date it was created with).</param>
    public static DraftResult Validate(PollDraft draft, PollLimits limits, int maxDurationDays = 0, DateTimeOffset? createdAt = null,
        PollDefinition? existing = null)
    {
        // The 100-year limit on dates counts from the creation day (today for a new poll).
        var today = DateOnly.FromDateTime((createdAt ?? DateTimeOffset.UtcNow).UtcDateTime);
        var result = PollDraftValidator.Validate(draft, limits, existing, today);
        var extra = new List<DraftError>();
        var closesOn = draft.ClosesOn;
        if (closesOn is { } day && day != existing?.ClosesOn && createdAt is { } created && LatestClose(maxDurationDays, created) is { } latest
            && day > latest)
            extra.Add(new DraftError("closesOn", $"Pick a date on or before {latest:yyyy-MM-dd} (at most {maxDurationDays} days after creation)."));
        var zone = draft.TimeZone?.Trim();
        if (!string.IsNullOrEmpty(zone) && PollRules.TimeZoneOk(zone) && !PollTime.IsKnown(zone))
            extra.Add(new DraftError("timezone", PollRules.InvalidTimeZone(zone)));
        if (extra.Count == 0) return result;
        return new DraftResult(null, [.. result.Errors, .. extra]);
    }
}
