using Telrang.Web.Data;
using Telrang.Web.Features.Settings;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// The per-account limits in force (DESIGN.md §14.4): the account's override when set, else the global
/// default. 0 means no limit. Accounts that registered themselves less than 24 h ago get half of the
/// creation limits (open polls, new polls per day; at least 1) unless an admin set an override.
/// Ballot, invite, duration and response limits apply to polls the account owns.
/// </summary>
public sealed record EffectiveLimits(int ActivePolls, int PollsPerDay, int BallotsPerPoll, int InvitesPerPoll, int PollDurationDays,
    int ResponsesPerVoter, bool Halved)
{
    public static readonly TimeSpan NewAccountAge = TimeSpan.FromHours(24);

    public static EffectiveLimits From(LimitSettings s, ApplicationUser? u, DateTimeOffset now)
    {
        var halve = u is { SelfRegistered: true } && now - u.CreatedAt < NewAccountAge;
        int Creation(int? over, int global) => over ?? (halve && global > 0 ? Math.Max(1, global / 2) : global);
        return new EffectiveLimits(
            Creation(u?.LimitActivePolls, s.ActivePollsPerUser),
            Creation(u?.LimitPollsPerDay, s.PollCreationsPerDay),
            u?.LimitBallotsPerPoll ?? s.MaxBallotsPerPoll,
            u?.LimitInvitesPerPoll ?? s.MaxInviteTokensPerPoll,
            Math.Min(u?.LimitPollDurationDays ?? s.MaxPollDurationDays, LimitSettings.PollDurationCeilingDays), // overrides too (R2-POLLS-7)
            u?.LimitResponsesPerVoter ?? s.ResponsesPerVoter,
            halve);
    }

}

public static class UserLimits
{
    public static async Task<EffectiveLimits> ForUserAsync(ApplicationDbContext db, LimitSettings s, string userId, DateTimeOffset now, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return EffectiveLimits.From(s, user, now);
    }

    /// <summary>One-line refusals: the limit and nothing else (DESIGN.md §14.2 UI copy).</summary>
    public static string ActivePollsMessage(int n) => $"Limit reached: {n} open {(n == 1 ? "poll" : "polls")}. Close one first.";
    public static string PollsPerDayMessage(int n) => $"Limit reached: {n} new {(n == 1 ? "poll" : "polls")} per 24 hours.";
    public static string BallotsMessage(int n) => $"Limit reached: {n} {(n == 1 ? "ballot" : "ballots")} per poll.";
    public static string ResponsesMessage(int n) => $"Limit reached: {n} {(n == 1 ? "response" : "responses")} per voter.";
    public static string InvitesMessage(int n, int left) => $"Limit reached: {n} invite links per poll ({left} left).";
}
