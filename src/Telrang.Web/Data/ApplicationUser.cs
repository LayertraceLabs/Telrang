using Microsoft.AspNetCore.Identity;

namespace Telrang.Web.Data;

/// <summary>
/// An account. Email is the login name (UserName == Email) once set up. Pending and deleted accounts
/// have a null Email and UserName == Id (a GUID string), so nobody can sign in as them.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public const int MaxDisplayNameLength = 60;

    /// <summary>Default display name for ballots (≤ 60 chars).</summary>
    [PersonalData]
    public string? DisplayName { get; set; }

    /// <summary>The header shows a generic avatar instead of the name (DESIGN.md §24.2).</summary>
    public bool HideIdentity { get; set; }

    /// <summary>IANA time zone id, e.g. "Europe/Berlin".</summary>
    [PersonalData]
    public string? TimeZone { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Soft delete: row kept so references stay valid; email, password and 2FA are gone.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Created by an admin; email and password are set through a setup link.</summary>
    public bool PendingSetup { get; set; }

    /// <summary>Must enrol an authenticator app before the session is usable (setup flows, admin "disable 2FA").</summary>
    public bool MustEnrolTwoFactor { get; set; }

    /// <summary>Created through open registration (halved creation limits for the first 24 h, DESIGN.md §14.7).</summary>
    public bool SelfRegistered { get; set; }

    // Per-account limit overrides (admin Users tab, DESIGN.md §14.4). Null = the global default; 0 = no limit.
    public int? LimitActivePolls { get; set; }
    public int? LimitPollsPerDay { get; set; }
    public int? LimitBallotsPerPoll { get; set; }
    public int? LimitInvitesPerPoll { get; set; }
    public int? LimitPollDurationDays { get; set; }
    public int? LimitResponsesPerVoter { get; set; }
}
