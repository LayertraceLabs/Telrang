using System.Text.Json;
using Telrang.Web.Features.Settings;

namespace Telrang.Web.Security;

/// <summary>What the provider said about a token (pentest R2-VOTE-2).</summary>
public enum CaptchaResult
{
    /// <summary>The provider accepted the token.</summary>
    Accepted,

    /// <summary>The provider refused the token (invalid, expired, used): the voter's side; an abuse signal.</summary>
    Rejected,

    /// <summary>No verdict about the token: provider unreachable, timeout, server error, wrong secret. Never an abuse signal.</summary>
    Unavailable,
}

/// <summary>Server-side captcha check (the provider's siteverify endpoint). Faked in tests.</summary>
public interface ICaptchaVerifier
{
    /// <summary>The provider's verdict on <paramref name="token"/>. Never throws for provider errors.</summary>
    Task<CaptchaResult> VerifyAsync(CaptchaProvider provider, string secret, string token, CancellationToken ct = default);
}

/// <summary>
/// Provider facts in one place: script, CSP origins and verify endpoint. The voter's browser loads
/// the provider's script and frame, so the provider sees the voter's IP address (documented in
/// DESIGN.md §8 and on the admin Settings tab). The server never sends the voter's IP to it.
/// </summary>
public static class CaptchaProviders
{
    public static string ScriptUrl(CaptchaProvider provider) => provider switch
    {
        CaptchaProvider.Turnstile => "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit",
        CaptchaProvider.HCaptcha => "https://js.hcaptcha.com/1/api.js?render=explicit",
        _ => "",
    };

    public static string VerifyUrl(CaptchaProvider provider) => provider switch
    {
        CaptchaProvider.Turnstile => "https://challenges.cloudflare.com/turnstile/v0/siteverify",
        CaptchaProvider.HCaptcha => "https://api.hcaptcha.com/siteverify",
        _ => "",
    };

    /// <summary>Origins the CSP must allow for scripts, frames, styles and connections.</summary>
    public static string[] Origins(CaptchaProvider provider) => provider switch
    {
        CaptchaProvider.Turnstile => ["https://challenges.cloudflare.com"],
        CaptchaProvider.HCaptcha => ["https://hcaptcha.com", "https://*.hcaptcha.com"],
        _ => [],
    };

    /// <summary>Whether a vote needs a captcha: enabled, a <c>voters: public</c> poll and an anonymous voter.</summary>
    public static bool Required(CaptchaSettings captcha, Data.Poll poll, string? userId) =>
        captcha.Enabled && poll.VoterMode == Telrang.Core.Polls.VoterMode.Public && userId is null;
}

public sealed class HttpCaptchaVerifier(IHttpClientFactory clients, ILogger<HttpCaptchaVerifier> logger) : ICaptchaVerifier
{
    /// <summary>
    /// siteverify error codes that blame the site or the provider, not the token (Turnstile and hCaptcha): a refusal with
    /// one of these is <see cref="CaptchaResult.Unavailable"/>.
    /// </summary>
    private static readonly HashSet<string> ServerSideErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        "missing-input-secret", "invalid-input-secret", "bad-request", "internal-error", "sitekey-secret-mismatch",
        "invalid-or-missing-sitekey", "not-using-dummy-passcode", "invalid-remoteip",
    };

    public async Task<CaptchaResult> VerifyAsync(CaptchaProvider provider, string secret, string token, CancellationToken ct = default)
    {
        var url = CaptchaProviders.VerifyUrl(provider);
        if (url.Length == 0) return CaptchaResult.Unavailable;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return CaptchaResult.Rejected;
        try
        {
            using var client = clients.CreateClient(nameof(HttpCaptchaVerifier));
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = secret,
                ["response"] = token,
            }), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Captcha verify returned {Status}.", (int)response.StatusCode);
                return CaptchaResult.Unavailable;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return Classify(json.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Captcha verify failed: {Error}", e.Message);
            return CaptchaResult.Unavailable;
        }
    }

    /// <summary>A siteverify answer: success, a refusal of the token, or a refusal that blames the site's request.</summary>
    public CaptchaResult Classify(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return CaptchaResult.Unavailable;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) return CaptchaResult.Accepted;
        var codes = root.TryGetProperty("error-codes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList()
            : [];
        var serverSide = codes.Where(ServerSideErrors.Contains).ToList();
        if (serverSide.Count == 0) return CaptchaResult.Rejected;
        // Only the provider's fixed words from the list above reach the log, never the secret or the token.
        logger.LogWarning("Captcha verify refused the site's request: {Codes}", string.Join(", ", serverSide));
        return CaptchaResult.Unavailable;
    }
}
