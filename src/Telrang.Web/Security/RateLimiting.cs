using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Telrang.Web.Security;

/// <summary>
/// Per-IP sliding-window limits: the vote page POSTs (DESIGN.md §21.1), the sensitive account POSTs, page GETs
/// (DESIGN.md §31, pentest R3-RES-6: <c>RateLimiting:PageGetsPerMinute</c>, default 600 per client key, 0 = off; static
/// files, branding, the Blazor and health endpoints are not counted) and every other POST
/// (<c>RateLimiting:OtherPostsPerMinute</c>, default 120 per client key, 0 = off; not the Blazor hub). The Identity pages are all served by the single
/// Razor Components endpoint, so endpoint policies cannot target them; instead a global limiter matches POSTs to the
/// sensitive /Account paths. Heavy GETs also have a concurrency limit (<see cref="HeavyGetLimiter"/>).
/// </summary>
public static class RateLimiting
{
    /// <summary>Admin database backup: one per minute per admin.</summary>
    public const string BackupPolicy = "Backup";

    private static readonly HashSet<string> LimitedAccountPosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Login",
        "/Account/Register",
        "/Account/LoginWith2fa",
        "/Account/LoginWithRecoveryCode",
        "/Account/ForgotPassword",
        "/Account/ResetPassword",
        "/Account/ResendEmailConfirmation",
        // Confirming a registration takes the account's password (pentest R2-ACC-1).
        "/Account/ConfirmEmail",
        // Answers differently for accounts with and without passkeys; limit enumeration.
        "/Account/PasskeyRequestOptions",
        // Re-authentication with the password or a TOTP code (delete account, data download, 2FA off).
        "/Account/Manage/PersonalData",
        "/Account/Manage/DownloadPersonalData",
        "/Account/Manage/Disable2fa",
        "/Account/Manage/ResetAuthenticator",
        // Re-authentication or the old password when adding credentials (pentest AV-02).
        "/Account/Manage/ChangePassword",
        "/Account/Manage/Email",
        "/Account/Manage/Passkeys",
        "/Account/Manage/GenerateRecoveryCodes",
        "/Account/Manage/EnableAuthenticator",
    };

    // Setup-link pages (/setup/account/{token}) share the auth limit; /setup itself is stricter.
    private const string SetupLinkPrefix = "/setup/account";
    private const string AdminSetupPath = "/setup";
    private const string RecoverPath = "/setup/recover";

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var authPerMinute = config.GetValue("RateLimiting:AuthPermitPerMinute", 10);
        var setupPerMinute = config.GetValue("RateLimiting:SetupPermitPerMinute", 5);
        var votePerMinute = config.GetValue("RateLimiting:VotePermitPerMinute", 120);
        var pageGetsPerMinute = config.GetValue("RateLimiting:PageGetsPerMinute", 600);
        var otherPostsPerMinute = config.GetValue("RateLimiting:OtherPostsPerMinute", 120);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (ctx, ct) =>
            {
                TimeSpan? retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : null;
                var http = ctx.HttpContext;
                // Vote posts (DESIGN.md §21.1), page GETs and other posts are no abuse signal: a class or office behind one
                // address must not get blocked; the captcha and the per-poll limits handle bots.
                if (!IsVotePost(http) && !IsPageGet(http) && !IsOtherPost(http))
                {
                    http.RequestServices.GetService<AbuseTracker>()?.Record(ClientKeys.ForAbuse(http),
                        http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, AbuseSignal.RateLimited);
                }
                // Writing a body also stops UseStatusCodePagesWithReExecute from turning this into "Not found".
                await StaticPages.TooManyAsync(http, "Too many attempts. Wait a minute and try again.", retryAfter);
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                // Per IPv6 /48 like the abuse tracker (pentest R2-VOTE-1): a /64 each would let one /48 multiply the limit.
                if (IsVotePost(ctx)) return SlidingWindow("vote:" + ClientKeys.ForAbuse(ctx), votePerMinute);
                if (IsSetupPost(ctx)) return SlidingWindow("setup:" + ClientKey(ctx), setupPerMinute);
                if (IsAuthPost(ctx)) return SlidingWindow("auth:" + ClientKey(ctx), authPerMinute);
                if (pageGetsPerMinute > 0 && IsPageGet(ctx)) return SlidingWindow("get:" + ClientKeys.ForAbuse(ctx), pageGetsPerMinute);
                if (otherPostsPerMinute > 0 && IsOtherPost(ctx)) return SlidingWindow("post:" + ClientKeys.ForAbuse(ctx), otherPostsPerMinute);
                return RateLimitPartition.GetNoLimiter("unlimited");
            });

            options.AddPolicy(BackupPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                "backup:" + (ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? ClientKey(ctx)),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });

        return services;
    }

    private static readonly string[] UncountedPrefixes = ["/_blazor", "/_framework", "/_content", "/branding", "/healthz"];

    /// <summary>
    /// A GET or HEAD of a page (DESIGN.md §31): not a static file (a last path segment with a dot), not branding, Blazor
    /// or the health check. The CSV downloads count as pages.
    /// </summary>
    internal static bool IsPageGet(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)) return false;
        var path = ctx.Request.Path;
        foreach (var prefix in UncountedPrefixes) if (path.StartsWithSegments(prefix)) return false;
        var value = path.Value ?? "/";
        var last = value[(value.LastIndexOf('/') + 1)..];
        return !last.Contains('.', StringComparison.Ordinal) || last.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A request with a body method (POST, PUT, PATCH, DELETE) that no other partition takes: not a vote post, not the
    /// Blazor hub (circuits have their own limits, and long polling posts every circuit message).
    /// </summary>
    internal static bool IsOtherPost(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)) return false;
        return !IsVotePost(ctx) && !IsSetupPost(ctx) && !IsAuthPost(ctx) && !RequestLimits.IsHub(ctx);
    }

    /// <summary>A POST to <c>/setup</c> or <c>/setup/recover</c>.</summary>
    private static bool IsSetupPost(HttpContext ctx) => HttpMethods.IsPost(ctx.Request.Method) && TrimmedPath(ctx) is var path
        && (string.Equals(path, AdminSetupPath, StringComparison.OrdinalIgnoreCase) || string.Equals(path, RecoverPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>A POST to a sensitive account path or a setup-link page.</summary>
    private static bool IsAuthPost(HttpContext ctx) => HttpMethods.IsPost(ctx.Request.Method) && TrimmedPath(ctx) is var path
        && (LimitedAccountPosts.Contains(path) || path.StartsWith(SetupLinkPrefix + "/", StringComparison.OrdinalIgnoreCase));

    private static string TrimmedPath(HttpContext ctx) => ctx.Request.Path.Value?.TrimEnd('/') ?? "";

    /// <summary>Every POST under <c>/p/</c> (the vote page's forms).</summary>
    internal static bool IsVotePost(HttpContext ctx) => HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/p");

    // RemoteIpAddress is the real client once UseForwardedHeaders has run. IPv6 clients usually
    // own a whole /64 (often a /56 or /48), so they are bucketed per prefix rather than per address.
    internal static string ClientKey(HttpContext ctx, int ipv6PrefixBytes = 8) => ClientKeys.For(ctx.Connection.RemoteIpAddress, ipv6PrefixBytes);

    private static RateLimitPartition<string> SlidingWindow(string key, int permitPerMinute) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}

/// <summary>
/// Open Blazor circuits (pentest AV-09, R2-POLLS-6): concurrency limits per account for signed-in users
/// (<c>RateLimiting:CircuitsPerAccount</c>, default 20) and per client key (IPv4 address, IPv6 /48, or "unknown" when the
/// address is missing; <c>RateLimiting:CircuitsPerClient</c>, default 60, larger because a school or office shares one
/// address). Each circuit buffers up to <c>LimitSettings.MaxCircuitMessageBytes</c>. Memory only, like the request limiter.
/// </summary>
public sealed class CircuitLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> clients;
    private readonly PartitionedRateLimiter<string> accounts;

    public CircuitLimiter(IConfiguration config)
    {
        PerClient = Math.Max(1, config.GetValue("RateLimiting:CircuitsPerClient", 60));
        PerAccount = Math.Max(1, config.GetValue("RateLimiting:CircuitsPerAccount", 20));
        clients = LeaseSet.Partitioned(PerClient);
        accounts = LeaseSet.Partitioned(PerAccount);
    }

    public int PerClient { get; }
    public int PerAccount { get; }

    /// <summary>
    /// One lease for both limits, to hold while the circuit is open, or null when the client key or the account (when
    /// signed in) has its maximum open already.
    /// </summary>
    public IDisposable? TryAcquire(string clientKey, string? accountId) => accountId is { Length: > 0 }
        ? LeaseSet.TryAcquire(() => clients.AttemptAcquire(clientKey), () => accounts.AttemptAcquire(accountId))
        : LeaseSet.TryAcquire(() => clients.AttemptAcquire(clientKey));

    public void Dispose()
    {
        clients.Dispose();
        accounts.Dispose();
    }
}

/// <summary>Leases from several concurrency limiters, all or none; disposing releases each once.</summary>
internal sealed class LeaseSet(List<RateLimitLease> leases) : IDisposable
{
    private int disposed;

    public static IDisposable? TryAcquire(params Func<RateLimitLease>[] acquires)
    {
        var held = new List<RateLimitLease>(acquires.Length);
        foreach (var acquire in acquires)
        {
            var lease = acquire();
            if (!lease.IsAcquired)
            {
                lease.Dispose();
                foreach (var h in held) h.Dispose();
                return null;
            }
            held.Add(lease);
        }
        return new LeaseSet(held);
    }

    public static PartitionedRateLimiter<string> Partitioned(int permits) => PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions { PermitLimit = permits, QueueLimit = 0 }));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        foreach (var lease in leases) lease.Dispose();
    }
}

/// <summary>
/// Open live results streams (DESIGN.md §21.2): concurrency limits per client key (IPv4 address, IPv6 /48), per account
/// and in all, so held-open responses cannot exhaust the server. <c>RateLimiting:StreamsPerClient</c> (default 10),
/// <c>RateLimiting:StreamsPerAccount</c> (default 10), <c>RateLimiting:StreamsTotal</c> (default 1000). Memory only;
/// client keys are never logged or stored.
/// </summary>
public sealed class LiveStreamLimiter : IDisposable
{
    private readonly ConcurrencyLimiter total;
    private readonly PartitionedRateLimiter<string> clients;
    private readonly PartitionedRateLimiter<string> accounts;

    public LiveStreamLimiter(IConfiguration config)
    {
        PerClient = Math.Max(1, config.GetValue("RateLimiting:StreamsPerClient", 10));
        PerAccount = Math.Max(1, config.GetValue("RateLimiting:StreamsPerAccount", 10));
        Total = Math.Max(1, config.GetValue("RateLimiting:StreamsTotal", 1000));
        total = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = Total, QueueLimit = 0 });
        clients = LeaseSet.Partitioned(PerClient);
        accounts = LeaseSet.Partitioned(PerAccount);
    }

    public int PerClient { get; }
    public int PerAccount { get; }
    public int Total { get; }

    /// <summary>One lease for all three limits (dispose it when the stream ends), or null when any of them is full.</summary>
    public IDisposable? TryAcquire(string clientKey, string accountId) => LeaseSet.TryAcquire(
        () => total.AttemptAcquire(),
        () => clients.AttemptAcquire(clientKey),
        () => accounts.AttemptAcquire(accountId));

    public void Dispose()
    {
        total.Dispose();
        clients.Dispose();
        accounts.Dispose();
    }
}

/// <summary>
/// Saves that add to a poll's change log (DESIGN.md §31, pentest R3-VOTE-2 / R3-RES-3): a token bucket per poll,
/// <c>RateLimiting:LoggedEditsPerPoll</c> saves at once (default 10), one more every <c>RateLimiting:LoggedEditMinutes</c>
/// (default 6). Edits before the first ballot and edits that change nothing the log records are not counted. Memory only;
/// idle buckets are dropped by the limiter.
/// </summary>
public sealed class ChangeLogLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> polls;

    public ChangeLogLimiter(IConfiguration config)
    {
        Burst = Math.Max(1, config.GetValue("RateLimiting:LoggedEditsPerPoll", 10));
        Period = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("RateLimiting:LoggedEditMinutes", 6)));
        polls = PartitionedRateLimiter.Create<Guid, Guid>(id => RateLimitPartition.GetTokenBucketLimiter(id, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = Burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = Period,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
    }

    public int Burst { get; }
    public TimeSpan Period { get; }

    /// <summary>Takes one save from the poll's bucket; false when it is empty.</summary>
    public bool TryTake(Guid pollId)
    {
        using var lease = polls.AttemptAcquire(pollId);
        return lease.IsAcquired;
    }

    public const string Refused = "Wait a few minutes before changing this poll again.";

    public void Dispose() => polls.Dispose();
}

/// <summary>
/// Concurrency limits on the heavy GETs (DESIGN.md §31, pentest R3-RES-1): the results page (<c>/p/{id}/results</c>) and
/// the owner's CSV downloads. <c>RateLimiting:HeavyGetsPerClient</c> (default 4) at once per client key (IPv4 address,
/// IPv6 /48) and <c>RateLimiting:HeavyGetsTotal</c> (default twice the processor count, at least 4) in all. Over either:
/// 503 with <c>Retry-After</c>, no queue and no abuse signal (a crowd refreshing results is not an attack). Memory only.
/// </summary>
public sealed class HeavyGetLimiter : IDisposable
{
    private readonly ConcurrencyLimiter total;
    private readonly PartitionedRateLimiter<string> clients;

    public HeavyGetLimiter(IConfiguration config)
    {
        PerClient = Math.Max(1, config.GetValue("RateLimiting:HeavyGetsPerClient", 4));
        Total = Math.Max(1, config.GetValue("RateLimiting:HeavyGetsTotal", Math.Max(4, 2 * Environment.ProcessorCount)));
        total = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = Total, QueueLimit = 0 });
        clients = LeaseSet.Partitioned(PerClient);
    }

    public int PerClient { get; }
    public int Total { get; }

    /// <summary>Seconds a refused client is asked to wait.</summary>
    public const int RetryAfterSeconds = 2;

    /// <summary>One lease for both limits (dispose it when the response is done), or null when either is full.</summary>
    public IDisposable? TryAcquire(string clientKey) =>
        LeaseSet.TryAcquire(() => clients.AttemptAcquire(clientKey), () => total.AttemptAcquire());

    /// <summary>A GET or HEAD of the results page or a CSV download.</summary>
    public static bool IsHeavy(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)) return false;
        var parts = (ctx.Request.Path.Value ?? "").Trim('/').Split('/');
        return parts.Length == 3 && ((parts[0] == "p" && parts[2] == "results")
            || (parts[0] == "polls" && parts[2] is "export.csv" or "results.csv"));
    }

    public void Dispose()
    {
        total.Dispose();
        clients.Dispose();
    }
}

/// <summary>Applies <see cref="HeavyGetLimiter"/> to the heavy GETs; every other request passes.</summary>
public sealed class HeavyGetMiddleware(RequestDelegate next, HeavyGetLimiter limiter)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!HeavyGetLimiter.IsHeavy(http))
        {
            await next(http);
            return;
        }
        using var lease = limiter.TryAcquire(ClientKeys.ForAbuse(http));
        if (lease is null)
        {
            await StaticPages.WriteAsync(http, StatusCodes.Status503ServiceUnavailable, "Busy", "Too many requests for results at once. Try again in a few seconds.",
                TimeSpan.FromSeconds(HeavyGetLimiter.RetryAfterSeconds));
            return;
        }
        await next(http);
    }
}
