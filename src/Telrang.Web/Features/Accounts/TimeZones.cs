namespace Telrang.Web.Features.Accounts;

/// <summary>IANA time zone ids known to this host (Windows ids are converted).</summary>
public static class TimeZones
{
    private static readonly Lazy<IReadOnlyList<string>> all = new(() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : null)
            .Append("UTC")
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList());

    public static IReadOnlyList<string> All => all.Value;

    public static bool IsKnown(string? id) => id is not null && All.Contains(id, StringComparer.Ordinal);
}
