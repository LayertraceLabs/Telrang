using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Features.Results;

/// <summary>
/// <c>GET /p/{id}/results/live</c> (DESIGN.md §21.2): a server-sent event stream for the poll's owner only. Events carry
/// no data: <c>changed</c> (the results page should be fetched again) and <c>ping</c> (heartbeat). Anything but the
/// owner, signed in with an account that may act, is 404, like an unknown id. The access is checked again before every
/// event; a failed check, <see cref="PollChangeNotifier.End"/>, the lifetime or a shutdown ends the stream.
/// </summary>
public static class LiveResultsEndpoint
{
    /// <summary>At most one <c>changed</c> per this interval; changes in between coalesce.</summary>
    public static readonly TimeSpan MinChangeInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(25);

    /// <summary>A stream ends after this; the browser reconnects after <see cref="ReconnectionInterval"/> (and is checked again).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ReconnectionInterval = TimeSpan.FromSeconds(3);

    public const string ChangedEvent = "changed";
    public const string PingEvent = "ping";

    public static IEndpointRouteBuilder MapLiveResults(this IEndpointRouteBuilder app)
    {
        // AllowAnonymous only so that the fallback policy does not answer an anonymous or gated visitor with a login or
        // enrolment redirect: the handler itself answers 404 to everyone but an owner that may act.
        app.MapGet("/p/{id}/results/live", HandleAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> HandleAsync(string id, HttpContext http, UserManager<ApplicationUser> users,
        IDbContextFactory<ApplicationDbContext> dbFactory, PollAccessService access, PollChangeNotifier notifier,
        LiveStreamLimiter limiter, AbuseTracker abuse, BlockedRangeService ranges, TimeProvider time,
        IOptions<IdentityOptions> identityOptions, IHostApplicationLifetime lifetime, ILoggerFactory loggers)
    {
        if (users.GetUserId(http.User) is not { } userId) return TypedResults.NotFound();
        var check = new AccessCheck(id, userId, http.User.IsInRole(AppRoles.Admin), http.Connection.RemoteIpAddress,
            http.User.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType),
            dbFactory, access, abuse, ranges, time, loggers.CreateLogger(typeof(LiveResultsEndpoint)));
        if (await check.RunAsync(http.RequestAborted) is not { } poll) return TypedResults.NotFound();

        if (limiter.TryAcquire(ClientKeys.ForAbuse(http), userId) is not { } lease)
        {
            // No abuse signal (pentest R2-VOTE-3c): owners behind one school NAT opening tabs are no attack, and a
            // signal here would block them site-wide. The cap alone bounds the cost.
            await StaticPages.TooManyAsync(http, "Too many open live views. Close some tabs and reload.");
            return TypedResults.Empty;
        }
        // Released when the request ends, whether or not the stream was ever enumerated.
        http.Response.RegisterForDispose(lease);

        // ServerSentEventsResult sets its own Cache-Control; these win because OnStarting runs after it.
        http.Response.OnStarting(() =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            return Task.CompletedTask;
        });
        // The formatter enumerates with RequestAborted; the compiler links it with ApplicationStopping (EnumeratorCancellation).
        return TypedResults.ServerSentEvents(StreamAsync(poll, check, notifier, time, lifetime.ApplicationStopping));
    }

    /// <summary>
    /// The event loop. Waits for the earliest of: a pending change (no earlier than <see cref="MinChangeInterval"/> after
    /// the last one), the next ping, the poll's close instant, the lifetime end. Every wait uses the app's
    /// <see cref="TimeProvider"/>, so tests can drive it with a fake clock.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<string>> StreamAsync(Poll poll, AccessCheck check, PollChangeNotifier notifier,
        TimeProvider time, [EnumeratorCancellation] CancellationToken ct)
    {
        using var sub = notifier.Subscribe(poll.Id);
        var started = time.GetUtcNow();
        var end = started + Lifetime;
        var nextPing = started + PingInterval;
        DateTimeOffset? lastChanged = null;
        var pending = false;
        var wasClosed = PollState.IsClosed(poll, started);

        // Sent at once: flushes the headers (the browser's open event) and sets the reconnection interval.
        yield return Item(PingEvent);

        while (!ct.IsCancellationRequested && !sub.Ended)
        {
            var now = time.GetUtcNow();
            if (now >= end) yield break;
            // A close by its date makes no write: announce it once when it passes.
            if (!wasClosed && PollState.IsClosed(poll, now))
            {
                wasClosed = true;
                pending = true;
            }

            var changeDue = pending && (lastChanged is not { } last || now >= last + MinChangeInterval);
            if (changeDue || now >= nextPing)
            {
                if (await check.RecheckAsync(ct) is not { } fresh || sub.Ended) yield break;
                // Close date or close state may have changed with the write that triggered this.
                poll = fresh;
                wasClosed = PollState.IsClosed(poll, now);
                if (changeDue)
                {
                    pending = false;
                    lastChanged = now;
                    yield return Item(ChangedEvent);
                }
                else
                {
                    yield return Item(PingEvent);
                }
                nextPing = now + PingInterval;
                continue;
            }

            var wake = Min(end, nextPing);
            if (pending && lastChanged is { } l) wake = Min(wake, l + MinChangeInterval);
            if (!wasClosed && poll.ClosesAt is { } closes) wake = Min(wake, closes);
            var signalled = await WaitAsync(sub, wake - now, pending, time, ct);
            if (signalled) pending = true;
        }
    }

    private static SseItem<string> Item(string eventType) =>
        new(string.Empty, eventType) { ReconnectionInterval = ReconnectionInterval };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>
    /// Waits up to <paramref name="delay"/> for a change notice (not when one is already pending). True when a notice
    /// was taken; false on timeout, end of the poll's streams or cancellation (the loop re-checks those).
    /// </summary>
    private static async Task<bool> WaitAsync(PollChangeNotifier.Subscription sub, TimeSpan delay, bool pending, TimeProvider time,
        CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var timer = Task.Delay(delay, time, cts.Token);
            if (pending)
            {
                await Task.WhenAny(timer, sub.Reader.Completion); // an end is noticed at once, a change waits its turn
                return false;
            }
            var read = sub.Reader.WaitToReadAsync(cts.Token).AsTask();
            var first = await Task.WhenAny(timer, read);
            await cts.CancelAsync(); // stops the other wait
            if (first == read && await read) return sub.Reader.TryRead(out _);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Owner + account checks for one stream, run on connect and before every event. Besides <c>CanEdit</c> and
    /// <see cref="AccountGate.MayActAsync"/> it ends the stream for what the request pipeline would have refused had the
    /// event been a new request: a blocked address or account (<see cref="AbuseBlockMiddleware"/>; admins exempt, as there),
    /// a locked account, and a security stamp that changed since the stream opened (sign-out everywhere, password reset).
    /// </summary>
    private sealed class AccessCheck(string encodedId, string userId, bool admin, IPAddress? address, string? stamp,
        IDbContextFactory<ApplicationDbContext> dbFactory, PollAccessService access, AbuseTracker abuse, BlockedRangeService ranges,
        TimeProvider time, ILogger logger)
    {
        public async Task<Poll?> RunAsync(CancellationToken ct)
        {
            try
            {
                if (!admin && (ranges.IsBlocked(address) || abuse.Find(address, userId) is not null)) return null;
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                if (!await AccountGate.MayActAsync(db, userId, ct)) return null;
                var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                    .Select(u => new { u.SecurityStamp, u.LockoutEnd }).SingleOrDefaultAsync(ct);
                if (user is null || user.SecurityStamp != stamp) return null;
                if (user.LockoutEnd is { } until && until > time.GetUtcNow()) return null;
                var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
                return a is { CanEdit: true } ? a.Poll : null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// <see cref="RunAsync"/> once the response has started: a database error (a PostgreSQL restart, a locked SQLite file)
        /// ends the stream quietly, as a failed check would; the browser reconnects and is checked again.
        /// </summary>
        public async Task<Poll?> RecheckAsync(CancellationToken ct)
        {
            try
            {
                return await RunAsync(ct);
            }
            catch (Exception e) when (DatabaseErrors.IsDatabaseError(e))
            {
                logger.LogInformation("Live results stream ended: the access check could not reach the database ({Error}).",
                    DatabaseErrors.Describe(e));
                return null;
            }
        }
    }
}
