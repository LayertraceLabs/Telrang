using System.Globalization;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// Poll time zones (DESIGN.md §14.9). A close date (yyyy-MM-dd) means the end of that day in the
/// poll's IANA time zone; times are shown in the viewer's zone when known, else in the poll's.
/// </summary>
public static class PollTime
{
    public const string Utc = "UTC";

    /// <summary>The zone for an IANA id, or null when the host does not know it (Windows ids are refused).</summary>
    public static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (string.Equals(id, Utc, StringComparison.Ordinal)) return TimeZoneInfo.Utc;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId ? zone : null;
    }

    public static bool IsKnown(string? id) => Find(id) is not null;

    /// <summary>The zone for <paramref name="id"/>, UTC when unknown or missing.</summary>
    public static TimeZoneInfo Zone(string? id) => Find(id) ?? TimeZoneInfo.Utc;

    /// <summary>The last instant of <paramref name="closesOn"/> in the zone (one tick before the next local midnight), in UTC.</summary>
    public static DateTimeOffset CloseInstant(DateOnly closesOn, string? zoneId)
    {
        var zone = Zone(zoneId);
        var midnight = closesOn.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Zones whose DST switch happens at midnight skip it: the day then ends at the first valid local time.
        for (var i = 0; i < 8 && zone.IsInvalidTime(midnight); i++) midnight = midnight.AddMinutes(30);
        var utc = TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero).AddTicks(-1);
    }

    /// <summary>The zone to show times in: the viewer's account setting when valid, else the poll's.</summary>
    public static string ViewerZone(string? viewerZone, string? pollZone) =>
        IsKnown(viewerZone) ? viewerZone! : IsKnown(pollZone) ? pollZone! : Utc;

    /// <summary>"2026-11-30 23:59 Europe/Berlin".</summary>
    public static string Format(DateTimeOffset instant, string? zoneId)
    {
        var zone = Zone(zoneId);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + (Find(zoneId) is null ? Utc : zoneId);
    }
}
