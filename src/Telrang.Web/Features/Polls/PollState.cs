using Telrang.Web.Data;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// The one definition of "closed" and of the frozen close instant (DESIGN.md §13, audit fixes). A poll is closed when
/// the owner or an admin closed it (<see cref="Poll.ClosedAt"/>) or its close instant has passed
/// (<see cref="Poll.ClosesAt"/>). <see cref="Poll.ClosesAt"/> is fixed at creation (the "Closes on" date, else
/// creation + the owner's max duration) and recomputed only when the owner changes the close date.
/// </summary>
public static class PollState
{
    public static bool IsClosed(Poll poll, DateTimeOffset now) => IsClosed(poll.ClosedAt, poll.ClosesAt, now);

    /// <summary>For projections that carry only the two columns.</summary>
    public static bool IsClosed(DateTimeOffset? closedAt, DateTimeOffset? closesAt, DateTimeOffset now) =>
        closedAt is not null || (closesAt is { } closes && closes <= now);

    /// <summary>When the poll closed (or will close by its date): the earlier of the two; null = no close at all.</summary>
    public static DateTimeOffset? ClosedOn(DateTimeOffset? closedAt, DateTimeOffset? closesAt) =>
        closedAt is { } a && closesAt is { } b ? (a < b ? a : b) : closedAt ?? closesAt;

    /// <summary>The automatic close of a poll without a close date: creation + max duration; null when the limit is 0 (no auto-close).</summary>
    public static DateTimeOffset? AutoCloseAt(DateTimeOffset createdAt, int maxDurationDays) =>
        maxDurationDays > 0 ? createdAt + TimeSpan.FromDays(maxDurationDays) : null;
}
