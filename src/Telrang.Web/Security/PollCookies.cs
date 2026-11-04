using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Telrang.Core.Ids;
using Microsoft.AspNetCore.DataProtection;

namespace Telrang.Web.Security;

/// <summary>
/// What the server knows about the current visitor of one poll, independent of transport. Only
/// <see cref="PollAccessService"/> turns it into a <see cref="VoterIdentity"/> (DESIGN.md §16): a signed-in
/// visitor is the account and its server-side state; the cookie fields count only for anonymous visitors.
/// </summary>
/// <param name="UserId">Signed-in user, or null.</param>
/// <param name="LinkVoterKey">Raw voter key from the vk cookie (anonymous public mode). Never stored: ballots use
/// the per-poll HMAC from <see cref="VoterKeys"/>.</param>
/// <param name="InviteTokenHash">Invite token hash from the per-poll invite cookie or ?t= (invite mode; a signed-in
/// account claims an unclaimed token on its first write).</param>
/// <remarks>Always built from the request (cookies and the signed-in user), never from a posted form (DESIGN.md §21.1).</remarks>
public sealed record VisitorContext(string? UserId, string? LinkVoterKey, string? InviteTokenHash)
{
    public static VisitorContext ForUser(string? userId) => new(userId, null, null);
}

/// <summary>Bound in Program: whether poll cookies are Secure-only (same policy as the auth cookie).</summary>
public sealed class PollCookieOptions
{
    public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.Always;
}

/// <summary>
/// The two voter-side cookies (DESIGN.md §16), both Data-Protection protected and purpose-bound:
/// <list type="bullet">
/// <item><c>pi_{id}</c>: invite token hash for this poll and the user id it was issued under (null = anonymous), 1 year,
/// path /p/{id}. Read only by that same principal, so the next person on a shared browser (another account, or
/// anonymous after a sign-out) never uses it; the vote page deletes it for them.</item>
/// <item><c>vk</c>: random 128-bit voter key for anonymous public-mode voters, 1 year, path /.</item>
/// </list>
/// </summary>
public sealed class PollCookies(IDataProtectionProvider dataProtection, Microsoft.Extensions.Options.IOptions<PollCookieOptions> options)
{
    public const string VoterCookie = "vk";
    public static readonly TimeSpan VoterLifetime = TimeSpan.FromDays(365);

    private readonly IDataProtector voterProtector = dataProtection.CreateProtector("Telrang.VoterKey.v1");
    private readonly IDataProtector inviteProtector = dataProtection.CreateProtector("Telrang.InviteCookie.v1");

    public static string InviteCookieName(string encodedId) => "pi_" + encodedId;
    private static string PollPath(string encodedId) => "/p/" + encodedId;

    // ---- reading -----------------------------------------------------------------------------

    /// <summary>
    /// Builds the visitor context for one poll from a cookie lookup (HTTP request or test jar). For a signed-in
    /// user the vk cookie is not read: their state is the account's (DESIGN.md §16). The invite
    /// cookie is, because an unclaimed token is how an account first reaches its invitation, but only when it was
    /// issued to this same principal (the account that opened the link, or anonymous).
    /// </summary>
    public VisitorContext Read(Func<string, string?> cookie, Guid pollId, string? userId)
    {
        var encodedId = PollId.Encode(pollId);
        var anonymous = userId is null;
        return new VisitorContext(
            userId,
            anonymous ? ReadVoterKey(cookie(VoterCookie)) : null,
            ReadInvite(cookie(InviteCookieName(encodedId)), pollId, userId));
    }

    public VisitorContext Read(HttpContext http, Guid pollId, string? userId) =>
        Read(name => http.Request.Cookies[name], pollId, userId);

    public string? ReadVoterKey(string? value)
    {
        var key = Unprotect<string>(voterProtector, value);
        return key is { Length: > 0 and <= 64 } ? key : null;
    }

    /// <summary>The token hash, when the cookie is for this poll and was issued to <paramref name="userId"/> (null = anonymous).</summary>
    public string? ReadInvite(string? value, Guid pollId, string? userId)
    {
        var payload = Unprotect<InvitePayload>(inviteProtector, value);
        return payload is not null && payload.PollId == pollId && payload.UserId == userId ? payload.TokenHash : null;
    }

    // ---- writing -----------------------------------------------------------------------------

    /// <param name="userId">The signed-in account that opened the link, or null: the cookie works only for that principal.</param>
    public void IssueInvite(HttpContext http, Guid pollId, string tokenHash, string? userId)
    {
        var encodedId = PollId.Encode(pollId);
        http.Response.Cookies.Append(InviteCookieName(encodedId), Protect(inviteProtector, new InvitePayload(pollId, tokenHash, userId)),
            CookieOptions(http, PollPath(encodedId), VoterLifetime));
    }

    /// <summary>
    /// Deletes the invite cookie: a signed-in account whose claimed token makes it unnecessary, or a cookie issued to
    /// another principal. (Sign-in and sign-out cannot delete it: the path-scoped cookie never reaches /Account.)
    /// </summary>
    public static void ForgetInvite(HttpContext http, string encodedId)
    {
        if (!http.Response.HasStarted) http.Response.Cookies.Delete(InviteCookieName(encodedId), new CookieOptions { Path = PollPath(encodedId) });
    }

    /// <summary>Returns the visitor's link-mode voter key, creating the vk cookie if it is missing or invalid.</summary>
    public string EnsureVoterKey(HttpContext http)
    {
        if (ReadVoterKey(http.Request.Cookies[VoterCookie]) is { } existing) return existing;
        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        http.Response.Cookies.Append(VoterCookie, Protect(voterProtector, key), CookieOptions(http, "/", VoterLifetime));
        return key;
    }

    /// <summary>Deletes the vk cookie (login and logout rotate the link-mode identity).</summary>
    public static void ForgetVoterKey(HttpContext http)
    {
        if (!http.Response.HasStarted) http.Response.Cookies.Delete(VoterCookie, new CookieOptions { Path = "/" });
    }

    private CookieOptions CookieOptions(HttpContext http, string path, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = options.Value.SecurePolicy == CookieSecurePolicy.Always || http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = path,
        MaxAge = lifetime,
        IsEssential = true,
    };

    // ---- helpers -----------------------------------------------------------------------------

    private static string Protect<T>(IDataProtector protector, T payload) => protector.Protect(JsonSerializer.Serialize(payload));

    private static T? Unprotect<T>(IDataProtector protector, string? value) where T : class
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096) return null;
        try { return JsonSerializer.Deserialize<T>(protector.Unprotect(value)); }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { return null; }
    }

    /// <param name="UserId">The account the cookie was issued to; null = anonymous (and for cookies from before the binding).</param>
    private sealed record InvitePayload(Guid PollId, string TokenHash, string? UserId = null);
}
