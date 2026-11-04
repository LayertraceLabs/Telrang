using Telrang.Web.Features.Settings;

namespace Telrang.Web.Features.Voting;

/// <summary>
/// The vote page's request limits (DESIGN.md §21.1), applied as endpoint metadata so antiforgery and the form guard read
/// the form under them too. A ranked ballot sends at most two values per option (position, ban); the rest are the
/// fixed fields (antiforgery, handler, stamp, honeypot, form id, response, name, notify, layout, captcha tokens).
/// </summary>
public static class VoteFormLimits
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxValues = 2 * LimitSettings.OptionsCeiling + 32;
    /// <summary>Longest single value: a captcha token (a few kB) is the longest legitimate one.</summary>
    public const int MaxValueLength = 16 * 1024;
}
