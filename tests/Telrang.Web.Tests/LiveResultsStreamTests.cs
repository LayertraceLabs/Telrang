using System.Net;
using System.Threading.Channels;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §21.2: <c>GET /p/{id}/results/live</c>, the owner's event stream.</summary>
public sealed class LiveResultsStreamTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly SseEvent Ping = new("ping", "", "3000");

    private static Task<HttpResponseMessage> OpenAsync(HttpClient client, string id, string? peer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/p/{id}/results/live");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (peer is not null) request.Headers.Add(PeerHeader, peer);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>One parsed event: its type, data and retry field.</summary>
    private sealed record SseEvent(string? Type, string? Data, string? Retry);

    /// <summary>Reads the stream in the background into a channel; <see cref="NextAsync"/> returns null at the end.</summary>
    private sealed class Reader : IAsyncDisposable
    {
        private readonly HttpResponseMessage response;
        private readonly Channel<SseEvent?> events = Channel.CreateUnbounded<SseEvent?>();
        private readonly CancellationTokenSource stop = new();
        private readonly Task pump;

        public Reader(HttpResponseMessage response)
        {
            this.response = response;
            pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            try
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token));
                string? type = null, data = null, retry = null;
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    if (line.Length == 0)
                    {
                        events.Writer.TryWrite(new SseEvent(type, data, retry));
                        type = data = retry = null;
                    }
                    else if (line.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].TrimStart(' ');
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].TrimStart(' ');
                    else if (line.StartsWith("retry:", StringComparison.Ordinal)) retry = line[6..].TrimStart(' ');
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            events.Writer.TryWrite(null); // end of stream
            events.Writer.TryComplete();
        }

        /// <summary>The next event; null when the stream ended. Throws when nothing came within <see cref="Patience"/>.</summary>
        public async Task<SseEvent?> NextAsync()
        {
            using var cts = new CancellationTokenSource(Patience);
            return await events.Reader.ReadAsync(cts.Token);
        }

        /// <summary>True when no event (and no end) arrives within <see cref="Quiet"/>.</summary>
        public async Task<bool> QuietAsync()
        {
            await Task.Delay(Quiet);
            return !events.Reader.TryPeek(out _);
        }

        /// <summary>Moves the clock in steps until an event (or the end) arrives.</summary>
        public async Task<SseEvent?> NextAdvancingAsync(ManualTime clock, TimeSpan step, int maxSteps = 200)
        {
            for (var i = 0; i < maxSteps; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                try { return await events.Reader.ReadAsync(cts.Token); }
                catch (OperationCanceledException) { clock.Advance(step); }
            }
            throw new TimeoutException("No event while advancing the clock.");
        }

        public async ValueTask DisposeAsync()
        {
            if (stop.IsCancellationRequested) return;
            await stop.CancelAsync();
            response.Dispose();
            await pump;
        }
    }

    /// <summary>A clock whose timers (Task.Delay with this provider) fire only when the test advances it.</summary>
    public sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock gate = new();
        private readonly List<Timer> timers = [];
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate) return now;
        }

        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (gate)
            {
                now += by;
                due = timers.Where(t => t.Due is { } d && d <= now).ToList();
            }
            foreach (var t in due) t.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (gate) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan period = Timeout.InfiniteTimeSpan;

            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    this.period = period;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                }
                if (dueTime == TimeSpan.Zero) Fire();
                return true;
            }

            public void Fire()
            {
                lock (owner.gate)
                {
                    if (Due is null) return;
                    Due = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : owner.now + period;
                }
                callback(state);
            }

            public void Dispose()
            {
                lock (owner.gate)
                {
                    Due = null;
                    owner.timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async Task<(AppFactory Factory, ManualTime Clock, string OwnerId)> StartAsync(
        IReadOnlyDictionary<string, string?>? overrides = null, DateTimeOffset? start = null, bool fakePeer = false)
    {
        var clock = new ManualTime(start ?? new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var factory = new AppFactory(overrides, services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            if (fakePeer) AddFakePeer(s);
        });
        var ownerId = await CreateUserAsync(factory, "o@example.com");
        return (factory, clock, ownerId);
    }

    private static async Task<Reader> OpenOkAsync(HttpClient client, string id, string? peer = null)
    {
        var response = await OpenAsync(client, id, peer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reader = new Reader(response);
        Assert.Equal(Ping, await reader.NextAsync()); // sent at once: headers flushed, retry set
        return reader;
    }

    private static async Task VoteAsync(AppFactory factory, string id, string voterKey)
    {
        Assert.True(PollId.TryDecode(id, out var raw));
        var json = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking()
            .Where(p => p.Id == raw).Select(p => p.DefinitionJson).SingleAsync());
        var def = PollJson.DeserializeDefinition(json);
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, voterKey, null),
            new RankedBallot([def.Questions[0].Options[0].Key], []))).Succeeded);
    }

    [Fact]
    public async Task Owner_gets_the_stream_with_its_headers_and_a_vote_sends_changed()
    {
        var (factory, _, ownerId) = await StartAsync();
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft());
        var owner = await LoggedInBrowserAsync(factory, "o@example.com");

        var response = await OpenAsync(owner, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        await using var reader = new Reader(response);
        Assert.Equal(Ping, await reader.NextAsync());

        await VoteAsync(factory, id, "v1");
        Assert.Equal(new SseEvent("changed", "", "3000"), await reader.NextAsync());
        Assert.True(await reader.QuietAsync());
    }

    [Fact]
    public async Task Anyone_but_the_owner_gets_404()
    {
        var (factory, _, ownerId) = await StartAsync();
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft(results: ResultsVisibility.Open));
        await CreateUserAsync(factory, "x@example.com");
        var other = await LoggedInBrowserAsync(factory, "x@example.com");
        var owner = await LoggedInBrowserAsync(factory, "o@example.com");

        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(factory.CreateBrowser(), id)).StatusCode); // anonymous
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(other, id)).StatusCode); // may see the results, is not the owner
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(owner, PollId.Encode(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(owner, "not-an-id")).StatusCode);

        // The owner's account gated (must enrol 2FA): 404 as well, not the enrolment redirect.
        await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.Where(u => u.Id == ownerId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.MustEnrolTwoFactor, true)));
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(owner, id)).StatusCode);
    }

    [Fact]
    public async Task A_burst_coalesces_into_one_changed_per_two_seconds()
    {
        var (factory, clock, ownerId) = await StartAsync();
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft());
        await using var reader = await OpenOkAsync(await LoggedInBrowserAsync(factory, "o@example.com"), id);

        await VoteAsync(factory, id, "v1");
        Assert.Equal("changed", (await reader.NextAsync())!.Type);
        for (var i = 2; i <= 5; i++) await VoteAsync(factory, id, "v" + i);
        Assert.True(await reader.QuietAsync()); // within 2 s of the last changed: held back

        Assert.Equal("changed", (await reader.NextAdvancingAsync(clock, TimeSpan.FromMilliseconds(500)))!.Type);
        Assert.True(await reader.QuietAsync()); // four votes, one event
    }

    [Fact]
    public async Task Delete_and_transfer_end_the_stream_and_the_reconnect_is_404()
    {
        var (factory, _, ownerId) = await StartAsync();
        await using var _f = factory;
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        await CreateUserAsync(factory, "t@example.com");
        var owner = await LoggedInBrowserAsync(factory, "o@example.com");

        var deleted = await CreatePollAsync(factory, ownerId, RankedDraft());
        await using (var reader = await OpenOkAsync(owner, deleted))
        {
            Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().DeletePollAsync(ownerId, deleted)));
            Assert.Null(await reader.NextAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(owner, deleted)).StatusCode);

        var moved = await CreatePollAsync(factory, ownerId, RankedDraft());
        await using (var reader = await OpenOkAsync(owner, moved))
        {
            Assert.True((await WithScope(factory, sp => sp.GetRequiredService<AdminService>().TransferPollAsync(admin, moved, "t@example.com"))).Ok);
            Assert.Null(await reader.NextAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(owner, moved)).StatusCode);
    }

    [Fact]
    public async Task Access_is_checked_again_before_each_event()
    {
        var (factory, clock, ownerId) = await StartAsync();
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft());
        var owner = await LoggedInBrowserAsync(factory, "o@example.com");
        var abuse = factory.Services.GetRequiredService<AbuseTracker>();

        // Account blocked by an admin while the stream is open: the next change ends the stream instead of being sent.
        await using (var reader = await OpenOkAsync(owner, id))
        {
            var block = abuse.AddAccount(ownerId, "o", TimeSpan.FromMinutes(10), null);
            await VoteAsync(factory, id, "v1");
            Assert.Null(await reader.NextAsync());
            Assert.True(abuse.Clear(block.Id));
        }

        // Account gated meanwhile: the next heartbeat ends the stream.
        await using (var reader = await OpenOkAsync(owner, id))
        {
            await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.Where(u => u.Id == ownerId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.MustEnrolTwoFactor, true)));
            Assert.Null(await reader.NextAdvancingAsync(clock, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_the_stream()
    {
        var (factory, _, ownerId) = await StartAsync();
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft());
        await using var reader = await OpenOkAsync(await LoggedInBrowserAsync(factory, "o@example.com"), id);
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.UpdateSecurityStampAsync((await users.FindByIdAsync(ownerId))!);
        });
        await VoteAsync(factory, id, "v1");
        Assert.Null(await reader.NextAsync());
    }

    [Fact]
    public async Task Pings_keep_the_stream_alive_until_its_lifetime_ends()
    {
        var (factory, clock, ownerId) = await StartAsync();
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft());
        await using var reader = await OpenOkAsync(await LoggedInBrowserAsync(factory, "o@example.com"), id);
        var opened = clock.GetUtcNow();

        var pings = 0;
        while (await reader.NextAdvancingAsync(clock, TimeSpan.FromSeconds(5)) is { } e)
        {
            Assert.Equal(Ping, e);
            pings++;
        }
        Assert.True(clock.GetUtcNow() - opened >= LiveResultsEndpoint.Lifetime);
        Assert.InRange(pings, 20, 24); // every 25 s for 10 minutes
    }

    [Fact]
    public async Task Passing_the_close_time_sends_changed_once()
    {
        // 23:55 UTC; the poll closes at the end of the day.
        var (factory, clock, ownerId) = await StartAsync(start: new DateTimeOffset(2026, 11, 15, 23, 55, 0, TimeSpan.Zero));
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft().Closes("2026-11-15").Zone("UTC"));
        await using var reader = await OpenOkAsync(await LoggedInBrowserAsync(factory, "o@example.com"), id);

        SseEvent? e;
        while ((e = await reader.NextAdvancingAsync(clock, TimeSpan.FromSeconds(20))) is { Type: "ping" }) { }
        Assert.Equal("changed", e!.Type);
        Assert.True(clock.GetUtcNow() >= new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        // Only once: what follows are pings.
        Assert.Equal(Ping, await reader.NextAdvancingAsync(clock, TimeSpan.FromSeconds(20)));
        Assert.Equal(Ping, await reader.NextAdvancingAsync(clock, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Only_the_owners_results_view_loads_the_live_module()
    {
        var (factory, _, ownerId) = await StartAsync();
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft(results: ResultsVisibility.Open));
        await CreateUserAsync(factory, "x@example.com");

        var mine = await (await LoggedInBrowserAsync(factory, "o@example.com")).GetStringAsync($"/p/{id}/results");
        Assert.Contains($"data-live-src=\"p/{id}/results/live\"", mine);
        Assert.Contains("data-live-badge hidden", mine);
        Assert.Matches("<script type=\"module\" src=\"[^\"]*js/results-live[^\"]*\\.js\"></script>", mine);
        Assert.DoesNotContain("style=", mine);

        foreach (var viewer in new[] { factory.CreateBrowser(), await LoggedInBrowserAsync(factory, "x@example.com") })
        {
            var page = await (await GetPageAsync(viewer, $"/p/{id}/results")).Content.ReadAsStringAsync();
            Assert.Contains("data-live-results", page);
            Assert.DoesNotContain("data-live-src", page);
            Assert.DoesNotMatch("<script[^>]*results-live", page); // the import map lists every module; no script tag loads it
            Assert.DoesNotContain("data-live-badge", page);
        }

        var module = await factory.CreateBrowser().GetAsync("/js/results-live.js");
        Assert.Equal(HttpStatusCode.OK, module.StatusCode);
        var js = await module.Content.ReadAsStringAsync();
        Assert.DoesNotMatch(@"(innerHTML|outerHTML)\s*=|insertAdjacentHTML|document\.write", js);
        Assert.Contains("importNode", js);
    }

    [Fact]
    public async Task Streams_are_capped_per_client_per_account_and_in_all()
    {
        var (factory, _, ownerId) = await StartAsync(new Dictionary<string, string?>
        {
            ["RateLimiting:StreamsPerClient"] = "1",
            ["RateLimiting:StreamsPerAccount"] = "2",
            ["RateLimiting:StreamsTotal"] = "3",
        }, fakePeer: true);
        await using var _f = factory;
        var id = await CreatePollAsync(factory, ownerId, RankedDraft());
        var otherId = await CreateUserAsync(factory, "p@example.com");
        var other = await CreatePollAsync(factory, otherId, RankedDraft());
        var owner = await LoggedInBrowserAsync(factory, "o@example.com");
        var second = await LoggedInBrowserAsync(factory, "p@example.com");

        var a = await OpenOkAsync(owner, id, "203.0.113.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await OpenAsync(owner, id, "203.0.113.1")).StatusCode); // per client
        // R2-VOTE-3c: a refused stream is no abuse signal (owners behind one school NAT).
        for (var i = 0; i < 25; i++) (await OpenAsync(owner, id, "203.0.113.1")).Dispose();
        Assert.Null(factory.Services.GetRequiredService<AbuseTracker>().Find(IPAddress.Parse("203.0.113.1"), ownerId));
        await using var b = await OpenOkAsync(owner, id, "203.0.113.2");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await OpenAsync(owner, id, "203.0.113.3")).StatusCode); // per account
        await using var c = await OpenOkAsync(second, other, "203.0.113.4");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await OpenAsync(second, other, "203.0.113.5")).StatusCode); // in all

        // A closed stream frees its slot.
        await a.DisposeAsync();
        var reopened = false;
        for (var i = 0; i < 50 && !reopened; i++)
        {
            using var response = await OpenAsync(second, other, "203.0.113.6");
            reopened = response.StatusCode == HttpStatusCode.OK;
            if (!reopened) await Task.Delay(50);
        }
        Assert.True(reopened);
    }

    private sealed class FakeDbException() : DbException("The database is restarting (fake).");

    /// <summary>Fails every query while armed, as a database restart would.</summary>
    private sealed class FailQueries : DbCommandInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            Armed ? throw new FakeDbException() : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    [Fact]
    public async Task A_database_error_during_a_recheck_ends_the_stream_without_an_error_log()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var fail = new FailQueries();
        await using var factory = new AppFactory(services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(fail));
        });
        var ownerId = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, ownerId, RankedDraft());
        var owner = await LoggedInBrowserAsync(factory, "o@example.com");
        await using var reader = await OpenOkAsync(owner, id);

        fail.Armed = true;
        Assert.Null(await reader.NextAdvancingAsync(clock, TimeSpan.FromSeconds(5))); // the next ping's check fails: the end
        fail.Armed = false;
        var ring = factory.Services.GetRequiredService<Telrang.Web.Features.Status.LogRing>().Snapshot();
        // EF logs the failed command itself; nothing after it may (the server's unhandled-exception line, one per stream).
        Assert.DoesNotContain(ring, e => e.Level >= LogLevel.Error && !e.Category.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal));
    }
}
