using System.Globalization;
using Telrang.Web.Data;
using Telrang.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Features.Settings;

/// <summary>Keys of the <c>AppSetting</c> table. Missing rows mean "default".</summary>
public static class SettingKeys
{
    public const string RegistrationEnabled = "Registration.Enabled";
    /// <summary>§22.1: an admin-created account completed while mail is off stores its email as verified.</summary>
    public const string AutoVerifyWithoutMail = "Accounts.AutoVerifyWithoutMail";
    public const string DisplayName = "App.DisplayName";
    public const string PublicUrl = "App.PublicUrl";
    public const string LegalPageMarkdown = "Legal.PageMarkdown";

    public const string MailHost = "Mail.Host";
    public const string MailPort = "Mail.Port";
    public const string MailSecurity = "Mail.Security";
    public const string MailUsername = "Mail.Username";
    public const string MailPassword = "Mail.Password"; // protected
    public const string MailFromAddress = "Mail.FromAddress";
    public const string MailFromName = "Mail.FromName";
    public const string MailHourlyCap = "Mail.HourlyCap";
    public const string MailCooldownMinutes = "Mail.PerRecipientCooldownMinutes";
    public const string MailDailyMax = "Mail.PerRecipientDailyMax";

    public const string LimitActivePolls = "Limits.ActivePollsPerUser";
    public const string LimitPollsPerDay = "Limits.PollCreationsPerDay";
    public const string LimitBallotsPerPoll = "Limits.MaxBallotsPerPoll";
    public const string LimitInvitesPerPoll = "Limits.MaxInviteTokensPerPoll";
    public const string LimitPollDurationDays = "Limits.MaxPollDurationDays";
    public const string LimitTitle = "Limits.TitleLength";
    public const string LimitOptionLabel = "Limits.OptionLabelLength";
    public const string LimitOptionDescription = "Limits.OptionDescriptionLength";
    public const string LimitPollDescription = "Limits.PollDescriptionLength";
    public const string LimitOptions = "Limits.OptionsPerPoll";
    public const string LimitVoterName = "Limits.VoterNameLength";
    public const string LimitResponsesPerVoter = "Limits.ResponsesPerVoter";

    public const string CaptchaProvider = "Captcha.Provider";
    public const string CaptchaSiteKey = "Captcha.SiteKey";
    public const string CaptchaSecret = "Captcha.Secret"; // protected

    public const string ThemePreset = "Theme.Preset";
    /// <summary>Per-mode colour overrides (DESIGN.md §24.4); no row = the built-in colour.</summary>
    public const string ThemeLightBase = "Theme.LightBase";
    public const string ThemeLightHighlight = "Theme.LightHighlight";
    public const string ThemeDarkBase = "Theme.DarkBase";
    public const string ThemeDarkHighlight = "Theme.DarkHighlight";
    public const string ThemeFontSize = "Theme.FontSize";
    public const string ThemeFontStack = "Theme.FontStack";
    public const string ThemeCustomCss = "Theme.CustomCss";
    public const string ThemeBackgroundEnabled = "Theme.BackgroundEnabled";
    public const string ThemeBackgroundOverlay = "Theme.BackgroundOverlay";
    public const string ThemeLayout = "Theme.Layout";

    /// <summary>
    /// Keys of before §24.4, only read to upgrade a stored theme (ThemeSettings.From) and removed by the next save of the
    /// Theme section: palette choice, Custom colours (both modes), corner radius (ignored).
    /// </summary>
    public const string LegacyThemePalette = "Theme.Palette";
    public const string LegacyThemePrimary = "Theme.Primary";
    public const string LegacyThemeAccent = "Theme.Accent";
    public const string LegacyThemeRadius = "Theme.Radius";

    public const string HomeWelcomeMarkdown = "Home.WelcomeMarkdown";

    /// <summary>Days after closing that a poll is deleted automatically; missing or 0 = never (default).</summary>
    public const string RetentionDeleteClosedAfterDays = "Retention.DeleteClosedAfterDays";

    /// <summary>Stored protected with Data Protection (purpose <see cref="SettingsService.SecretPurpose"/>).</summary>
    public static readonly IReadOnlySet<string> Secrets = new HashSet<string> { MailPassword, CaptchaSecret };
}

public enum MailSecurity { None, StartTls, ImplicitTls }

/// <param name="HasPublicUrl">App.PublicUrl is set: every mailed link is built from it, never from a request (pentest AV-01).</param>
public sealed record MailSettings(
    string? Host, int Port, MailSecurity Security, string? Username, bool HasPassword,
    string? FromAddress, string? FromName, int HourlyCap, int CooldownMinutes, int DailyMax, bool HasPublicUrl = false)
{
    /// <summary>SMTP host and from address are set: the sender can deliver what is already queued.</summary>
    public bool HasTransport => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    /// <summary>Mail is usable: transport set and a Public URL for the links. Nothing is queued otherwise.</summary>
    public bool IsConfigured => HasTransport && HasPublicUrl;
}

/// <summary>
/// §14.4 defaults. The first five and <see cref="ResponsesPerVoter"/> are per-user limits (overridable per
/// account, 0 = no limit, see <c>UserLimits</c>); the rest are text and count caps for the poll validator.
/// </summary>
public sealed record LimitSettings(
    int ActivePollsPerUser, int PollCreationsPerDay, int MaxBallotsPerPoll, int MaxInviteTokensPerPoll,
    int MaxPollDurationDays, int TitleLength, int OptionLabelLength, int OptionDescriptionLength,
    int PollDescriptionLength, int OptionsPerPoll, int VoterNameLength, int ResponsesPerVoter = 10)
{
    /// <summary>(key, label, default, help) for the admin form; the help is the option's description (DESIGN.md §22.2).</summary>
    public static readonly IReadOnlyList<(string Key, string Label, int Default, string Help)> Fields =
    [
        (SettingKeys.LimitActivePolls, "Open polls per user", 20,
            "Polls one account may have open at the same time. Checked when a poll is created, reopened by its owner, or " +
            "given a later close date that opens it again; lowering it closes nothing. 0 = no limit. Accounts that registered " +
            "themselves get half (at least 1) during their first 24 hours. Per-account overrides on the Users tab replace it " +
            "and lift the halving."),
        (SettingKeys.LimitPollsPerDay, "New polls per user per day", 20,
            "Polls one account may create in any 24 hours. Deleting a poll does not give the slot back. 0 = no limit. " +
            "Halved (at least 1) for self-registered accounts during their first 24 hours. Overridable per account."),
        (SettingKeys.LimitBallotsPerPoll, "Ballots per poll", 1000,
            "Responses a poll accepts in total, from all voters. The poll owner's limit applies. A full poll refuses new " +
            "ballots; voters can still change the ones they cast, and no abuse signal is recorded. 0 = no limit. " +
            "Overridable per account (the owner's)."),
        (SettingKeys.LimitInvitesPerPoll, "Invite links per poll", 500,
            "Invite links a poll may hold, used or not; revoking a link frees its place. Checked when the owner creates links. " +
            "The owner's limit applies. 0 = no limit. Overridable per account."),
        (SettingKeys.LimitPollDurationDays, "Poll duration (days)", 180,
            "Latest close date, counted in days from the poll's creation. A poll without a close date closes automatically " +
            "after this many days. The close time is fixed at creation and recomputed only when the owner changes the close " +
            "date, so changing this setting moves no existing poll. 0 = no maximum (polls without a date stay open until " +
            "closed). At most 3650. Overridable per account (the owner's)."),
        (SettingKeys.LimitTitle, "Title length", 120,
            "Longest poll title in characters, for every account. Checked when a poll is created or saved; existing polls keep " +
            "a longer title until their next edit. At most 200 (it bounds the size of messages over the live connection). " +
            "0 refuses every poll, since a title is required."),
        (SettingKeys.LimitOptionLabel, "Option label length", 80,
            "Longest option label in characters, for every account. Checked on create and save. At most 200. 0 refuses every " +
            "option."),
        (SettingKeys.LimitOptionDescription, "Option description length", 1000,
            "Longest option description (Markdown) in characters, for every account. Checked on create and save. At most 1000. " +
            "0 allows no option descriptions."),
        (SettingKeys.LimitPollDescription, "Poll description length", 2000,
            "Longest poll description (Markdown) in characters, for every account. Checked on create and save. At most 10000. " +
            "0 allows no poll description."),
        (SettingKeys.LimitOptions, "Options per poll", 100,
            "Most options in one poll, for every account; in a dates poll each day or time slot is one option, and a dates poll " +
            $"has at most {Telrang.Core.Polls.PollRules.MaxDateOptions} whatever this is set to. At most {OptionsCeiling}. 0 refuses every poll."),
        (SettingKeys.LimitVoterName, "Voter name length", 60,
            "Longest name a voter can enter with a ballot, in characters. 10 to 120; values outside are refused on save."),
        (SettingKeys.LimitResponsesPerVoter, "Responses per voter (responses: many)", 10,
            "Responses one voter may submit to a poll that allows several (responses: many); changing an existing response " +
            "does not count. The poll owner's limit applies. 0 = no limit. Overridable per account (the owner's)."),
    ];

    /// <summary>
    /// Highest values the admin may set for the text and option caps (pentest AV-09). They bound the largest message a
    /// browser legitimately sends over the Blazor connection (<see cref="MaxCircuitMessageBytes"/>); stored values above
    /// them are read as the ceiling.
    /// </summary>
    /// <summary>The ceiling of <see cref="OptionsPerPoll"/> as a constant (the vote form's endpoint limits need one).</summary>
    public const int OptionsCeiling = 128;

    public static readonly IReadOnlyDictionary<string, int> Ceilings = new Dictionary<string, int>
    {
        [SettingKeys.LimitTitle] = 200,
        [SettingKeys.LimitOptionLabel] = 200,
        [SettingKeys.LimitOptionDescription] = 1000,
        [SettingKeys.LimitPollDescription] = 10_000,
        [SettingKeys.LimitOptions] = OptionsCeiling,
        [SettingKeys.LimitPollDurationDays] = PollDurationCeilingDays,
    };

    /// <summary>Longest max poll duration (ten years): a larger value made the close instant overflow (R2-POLLS-7).</summary>
    public const int PollDurationCeilingDays = 3650;

    /// <summary>
    /// SignalR receive limit for Blazor circuits: twice the editor's Text-mode text at the ceilings (every option with a
    /// full label and description, the same bound as the textarea's maxlength) for JSON escaping and multi-byte text, plus
    /// 16 kB for the rest of the message. The restored browser draft (draft.js) carries the same text. About 316 kB,
    /// instead of the 1 MB used before.
    /// </summary>
    public static int MaxCircuitMessageBytes =>
        2 * Ceilings[SettingKeys.LimitOptions] * (Ceilings[SettingKeys.LimitOptionLabel] + Ceilings[SettingKeys.LimitOptionDescription] + 2)
        + 16 * 1024;
}

public enum CaptchaProvider { None, Turnstile, HCaptcha }

/// <summary>Optional captcha for anonymous votes in <c>voters: public</c> polls (DESIGN.md §14.10 decision 4).</summary>
public sealed record CaptchaSettings(CaptchaProvider Provider, string? SiteKey, bool HasSecret)
{
    /// <summary>On only with a provider, a site key and a secret.</summary>
    public bool Enabled => Provider != CaptchaProvider.None && !string.IsNullOrWhiteSpace(SiteKey) && HasSecret;
}

public sealed record AppSettingsSnapshot(
    IReadOnlyDictionary<string, string?> Raw,
    bool RegistrationEnabled,
    string? DisplayName,
    string? PublicUrl,
    string? LegalPageMarkdown,
    MailSettings Mail,
    LimitSettings Limits,
    CaptchaSettings Captcha,
    Branding.ThemeSettings Theme,
    string? WelcomeMarkdown,
    int RetentionDays = 0,
    bool AutoVerifyWithoutMail = false)
{
    public int Int(string key, int fallback) =>
        Raw.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
}

/// <summary>
/// DB-backed settings (DESIGN.md §14.4/§14.5) read through an in-memory snapshot that every write
/// invalidates. Singleton; each load/write uses its own short-lived DbContext.
/// </summary>
public sealed class SettingsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    IOptionsMonitor<AppOptions> appOptions,
    TimeProvider time)
{
    public const string SecretPurpose = "Telrang.Settings.Secret.v1";

    private readonly IDataProtector secrets = dataProtection.CreateProtector(SecretPurpose);
    private readonly Lock loadLock = new();
    private volatile AppSettingsSnapshot? current;

    /// <summary>Cached snapshot; loads synchronously on first use after a write.</summary>
    public AppSettingsSnapshot Current
    {
        get
        {
            if (current is { } c) return c;
            lock (loadLock)
            {
                if (current is { } again) return again;
                using var db = dbFactory.CreateDbContext();
                var raw = db.AppSettings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
                return current = Build(raw);
            }
        }
    }

    /// <summary>
    /// Base URL for links shown on a page to the person who asked for them (share and invite links, an admin's setup or
    /// reset link): <c>App.PublicUrl</c> when set, else <paramref name="requestBaseUri"/>. Never for mailed links: use
    /// <see cref="MailLinkBase"/>.
    /// </summary>
    public string LinkBase(string requestBaseUri) => MailLinkBase ?? requestBaseUri;

    /// <summary>
    /// Base URL for links in mails: <c>App.PublicUrl</c> with a trailing slash, or null. A token-bearing link is never
    /// built from the request's Host header (pentest AV-01); without a Public URL no such mail is queued.
    /// </summary>
    public string? MailLinkBase => Current.PublicUrl is { } url ? url.TrimEnd('/') + "/" : null;

    /// <summary>
    /// Why <paramref name="url"/> cannot be the Public URL (null = it can, also when empty): an absolute https address
    /// without query or fragment; http only for a loopback host (pentest R2 Info b: mailed reset and setup links would
    /// otherwise travel in clear text).
    /// </summary>
    public static string? PublicUrlProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || !string.IsNullOrEmpty(u.UserInfo))
            return "Public URL: enter an address such as https://vote.example.com.";
        return u.Scheme == "http" && !IsLoopbackHost(u.Host) ? "Public URL: use https (http only for localhost)." : null;
    }

    /// <summary>
    /// False only when a Public URL is set and <paramref name="host"/> (a Host header without port) is neither its host
    /// nor a loopback name (health checks, tests, an SSH tunnel). Requests for other names are refused (pentest AV-01).
    /// </summary>
    public bool IsAllowedHost(string host)
    {
        if (Current.PublicUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return true;
        if (string.Equals(host, u.Host, StringComparison.OrdinalIgnoreCase) || string.Equals(host, u.IdnHost, StringComparison.OrdinalIgnoreCase))
            return true;
        return IsLoopbackHost(host);
    }

    public static bool IsLoopbackHost(string host)
    {
        host = host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
    }

    /// <summary>The user-facing app name: <c>App.DisplayName</c> when set, else config <c>App:Name</c>.</summary>
    public string AppName => Current.DisplayName ?? appOptions.CurrentValue.Name;

    /// <summary>
    /// Writes the given keys (null or blank = delete the row, i.e. back to default) and invalidates the
    /// cache. Secret keys are protected before they are stored.
    /// </summary>
    public async Task SetAsync(IReadOnlyDictionary<string, string?> values, string? updatedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = values.Keys.ToList();
        var rows = await db.AppSettings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
        var now = time.GetUtcNow();
        foreach (var (key, value) in values)
        {
            var blank = string.IsNullOrWhiteSpace(value);
            if (blank)
            {
                if (rows.TryGetValue(key, out var existing)) db.AppSettings.Remove(existing);
                continue;
            }
            var stored = SettingKeys.Secrets.Contains(key) ? secrets.Protect(value!) : value!.Trim();
            if (rows.TryGetValue(key, out var row))
            {
                row.Value = stored;
                row.UpdatedAt = now;
                row.UpdatedBy = updatedBy;
            }
            else
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = stored, UpdatedAt = now, UpdatedBy = updatedBy });
            }
        }
        await db.SaveChangesAsync(ct);
        current = null;
    }

    /// <summary>The decrypted SMTP password, or null. Only the mail transport needs it.</summary>
    public string? GetMailPassword() => GetSecret(SettingKeys.MailPassword);

    /// <summary>The decrypted captcha secret, or null. Only the captcha verifier needs it.</summary>
    public string? GetCaptchaSecret() => GetSecret(SettingKeys.CaptchaSecret);

    private string? GetSecret(string key)
    {
        if (!Current.Raw.TryGetValue(key, out var stored) || string.IsNullOrEmpty(stored)) return null;
        try { return secrets.Unprotect(stored); }
        catch (System.Security.Cryptography.CryptographicException) { return null; } // key ring lost
    }

    public void Invalidate() => current = null;

    private static AppSettingsSnapshot Build(Dictionary<string, string?> raw)
    {
        string? Str(string key) => raw.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        int Int(string key, int fallback) =>
            int.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 ? i : fallback;
        int Limit(string key)
        {
            var value = Int(key, LimitSettings.Fields.First(f => f.Key == key).Default);
            return LimitSettings.Ceilings.TryGetValue(key, out var max) ? Math.Min(value, max) : value;
        }

        var mail = new MailSettings(
            Str(SettingKeys.MailHost),
            Int(SettingKeys.MailPort, 587),
            Enum.TryParse<MailSecurity>(Str(SettingKeys.MailSecurity), true, out var sec) ? sec : MailSecurity.StartTls,
            Str(SettingKeys.MailUsername),
            Str(SettingKeys.MailPassword) is not null,
            Str(SettingKeys.MailFromAddress),
            Str(SettingKeys.MailFromName),
            Int(SettingKeys.MailHourlyCap, 100),
            Int(SettingKeys.MailCooldownMinutes, 15),
            Int(SettingKeys.MailDailyMax, 3),
            Str(SettingKeys.PublicUrl) is not null);

        var limits = new LimitSettings(
            Limit(SettingKeys.LimitActivePolls), Limit(SettingKeys.LimitPollsPerDay), Limit(SettingKeys.LimitBallotsPerPoll),
            Limit(SettingKeys.LimitInvitesPerPoll), Limit(SettingKeys.LimitPollDurationDays), Limit(SettingKeys.LimitTitle),
            Limit(SettingKeys.LimitOptionLabel), Limit(SettingKeys.LimitOptionDescription), Limit(SettingKeys.LimitPollDescription),
            Limit(SettingKeys.LimitOptions), Limit(SettingKeys.LimitVoterName),
            Limit(SettingKeys.LimitResponsesPerVoter));

        var captcha = new CaptchaSettings(
            Enum.TryParse<CaptchaProvider>(Str(SettingKeys.CaptchaProvider), true, out var provider) ? provider : CaptchaProvider.None,
            Str(SettingKeys.CaptchaSiteKey),
            Str(SettingKeys.CaptchaSecret) is not null);

        return new AppSettingsSnapshot(
            raw,
            bool.TryParse(Str(SettingKeys.RegistrationEnabled), out var reg) && reg,
            Str(SettingKeys.DisplayName),
            Str(SettingKeys.PublicUrl),
            Str(SettingKeys.LegalPageMarkdown),
            mail,
            limits,
            captcha,
            Branding.ThemeSettings.From(Str),
            Str(SettingKeys.HomeWelcomeMarkdown),
            Int(SettingKeys.RetentionDeleteClosedAfterDays, 0),
            bool.TryParse(Str(SettingKeys.AutoVerifyWithoutMail), out var autoVerify) && autoVerify);
    }
}

/// <summary>
/// Serves <see cref="IOptions{AppOptions}"/> with the DB display name applied, so every page that
/// injects AppOptions picks up <c>App.DisplayName</c> without code changes.
/// </summary>
public sealed class DynamicAppOptions(IOptionsMonitor<AppOptions> monitor, IServiceProvider services) : IOptions<AppOptions>
{
    public AppOptions Value
    {
        get
        {
            var config = monitor.CurrentValue;
            var settings = services.GetRequiredService<SettingsService>();
            return new AppOptions { Name = settings.AppName, DataDir = config.DataDir };
        }
    }
}
