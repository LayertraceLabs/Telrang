using System.Globalization;
using System.Security.Claims;

namespace Telrang.Web.Components.Layout;

/// <summary>
/// What the header shows for the signed-in account (DESIGN.md §24.2): never the email. The display name, else the part
/// of the email before <c>@</c>; nothing (a generic avatar) when the account hides it. Read from claims that
/// <c>AppClaimsPrincipalFactory</c> adds, so a render needs no database round trip; the profile page refreshes the
/// sign-in after a change and the security stamp validator re-creates other sessions' principals within its interval.
/// </summary>
public static class HeaderIdentity
{
    /// <summary>The account's display name, when set.</summary>
    public const string DisplayNameClaim = "av:display-name";

    /// <summary>Present ("1") when the account hides its name in the header (<c>ApplicationUser.HideIdentity</c>).</summary>
    public const string HideClaim = "av:hide-identity";

    /// <summary>The text for the header, or null when the account hides it (or the principal has no name at all).</summary>
    public static string? Text(ClaimsPrincipal user)
    {
        if (user.HasClaim(c => c.Type == HideClaim)) return null;
        if (user.FindFirst(DisplayNameClaim)?.Value is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name)) return name.Trim();
        return LocalPart(user.Identity?.Name);
    }

    /// <summary>The part of an email address before the last <c>@</c> (the whole value when it has none).</summary>
    public static string? LocalPart(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        var local = at > 0 ? email[..at] : at == 0 ? "" : email;
        return local.Length == 0 ? null : local;
    }

    /// <summary>The avatar letter: the first character of <paramref name="text"/>, upper case.</summary>
    public static string Initial(string text) =>
        StringInfo.GetNextTextElement(text.TrimStart()) is { Length: > 0 } first ? first.ToUpperInvariant() : "?";
}
