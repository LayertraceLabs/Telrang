using Telrang.Web.Features.Settings;

namespace Telrang.Web.Features.Voting;

/// <summary>
/// The vote page's request limits (DESIGN.md §21.1, §34.4), applied as endpoint metadata so antiforgery and the form guard
/// read the form under them too. A page's own fields get <see cref="PageFieldBytes"/> (the whole vote form before §34); a
/// multi-page form adds the carry field (<c>_carry</c>), whose largest value is computed from the limits below.
/// A ranked question sends at most two values per option (position, ban), so a page holds at most
/// <see cref="QuestionsMax"/> × 2 × the options ceiling of them; the rest are the fixed fields (antiforgery, handler, stamp,
/// honeypot, form id, response, carry, page buttons, name, notify, layout, captcha tokens).
/// </summary>
public static class VoteFormLimits
{
    /// <summary>A page's fields: the vote form's body limit before §34.</summary>
    public const int PageFieldBytes = 64 * 1024;

    /// <summary><c>PollLimits.QuestionsMax</c> (a test pins it): a page holds at most every question.</summary>
    public const int QuestionsMax = 50;

    /// <summary><c>PollLimits.ResponseTextBytesMax</c> (a test pins it): the free text of one response, UTF-8.</summary>
    public const int FreeTextBytes = 8192;

    /// <summary>
    /// Largest packed carry content: the scored answers of a response (bounded like a page's fields), its free text with the
    /// worst JSON escaping (6 bytes per byte, <c>\u00XX</c>), and 1 kB for the name and the rest.
    /// </summary>
    public const int CarryMaxBytes = PageFieldBytes + 6 * FreeTextBytes + 1024;

    /// <summary>Data Protection's header, IV, padding and MAC on top of the plaintext (about 100 bytes).</summary>
    private const int ProtectOverhead = 128;

    /// <summary>Longest carry field: base64url of the protected content.</summary>
    public const int CarryFieldMax = (CarryMaxBytes + ProtectOverhead + 2) / 3 * 4;

    public const int MaxBodyBytes = PageFieldBytes + CarryFieldMax;
    public const int MaxValues = QuestionsMax * 2 * LimitSettings.OptionsCeiling + 32;

    /// <summary>Longest single value: the carry field (a captcha token, a few kB, is the longest other one).</summary>
    public const int MaxValueLength = CarryFieldMax;
}
