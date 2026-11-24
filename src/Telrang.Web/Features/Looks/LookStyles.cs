using System.Collections.Concurrent;
using Telrang.Web.Data;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;

namespace Telrang.Web.Features.Looks;

/// <summary>
/// The stylesheets of looks (DESIGN.md §34.6, §34.7): a poll's <c>look.css</c> and the look forms' preview, both written by
/// <see cref="ThemeCss"/> from a validated <see cref="ThemeLook"/>, cached per look (a look is a value; the cache is bounded).
/// </summary>
public sealed class LookStyles(SettingsService settings)
{
    private const int CacheMax = 256;
    private readonly ConcurrentDictionary<ThemeLook, (string Css, string Version)> polls = new();
    private readonly ConcurrentDictionary<ThemeLook, string> previews = new();

    /// <summary>The admin setting "Poll looks" (on by default). Off: no poll links a look and <c>look.css</c> answers 404.</summary>
    public bool Enabled => settings.Current.PollLooks;

    public (string Css, string Version) PollCss(ThemeLook look)
    {
        if (polls.Count >= CacheMax) polls.Clear();
        return polls.GetOrAdd(look, l =>
        {
            var css = ThemeCss.PollLook(l);
            return (css, ThemeCss.Version(css));
        });
    }

    public string PreviewCss(ThemeLook look)
    {
        if (previews.Count >= CacheMax) previews.Clear();
        return previews.GetOrAdd(look, ThemeCss.Preview);
    }

    /// <summary>The poll's look as shown: none while the setting is off or when the poll has none (or a stored value fails the rules).</summary>
    public ThemeLook? Shown(Poll poll) => Enabled ? ThemeLook.FromStored(poll.LookJson) : null;

    /// <summary>
    /// <c>/p/{id}/look.css?v=&lt;hash&gt;</c> for a poll page whose access check passed, or null (no look shown). Only the
    /// <c>/p/*</c> pages link it (<see cref="PollLookLink"/>); pages on the <c>?admin=1</c> stylesheet never do.
    /// </summary>
    public string? Href(PollAccess access) =>
        access.CanView && Shown(access.Poll) is { } look ? $"/p/{access.EncodedId}/look.css?v={PollCss(look).Version}" : null;

    /// <summary>The look forms' preview sheet: <c>/looks/preview.css?c=&lt;look code&gt;</c>.</summary>
    public static string PreviewHref(ThemeLook look) => "looks/preview.css?c=" + Uri.EscapeDataString(look.ToCode());
}
