using System.Diagnostics;
using System.Net;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Fixes from the phase 3 security review (DESIGN.md §13, phase 4 entry) and the tests it asked for.</summary>
public class SecurityTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], ["berlin"]);

    // ---- A2: forwarded headers ----------------------------------------------------------------

    private static async Task<List<HttpStatusCode>> LoginPostsAsync(AppFactory factory, string peer, int count)
    {
        var client = factory.CreateBrowser();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login") { Content = new FormUrlEncodedContent([]) };
            request.Headers.Add(PeerHeader, peer);
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}"); // a new "client" every time
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }
        return statuses;
    }

    [Fact]
    public async Task Forwarded_for_from_a_public_address_is_ignored()
    {
        await using var factory = new AppFactory(services: AddFakePeer);
        // Untrusted peer: X-Forwarded-For is ignored, so all requests share the peer's bucket.
        var statuses = await LoginPostsAsync(factory, "203.0.113.7", 11);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Theory]
    [InlineData("127.0.0.1", "ReverseProxy:KnownProxies", "127.0.0.1")] // loopback: trusted only when configured (R3-DEP-7)
    [InlineData("203.0.113.7", "ReverseProxy:TrustAll", "true")]       // explicit ReverseProxy:TrustAll
    public async Task Forwarded_for_from_a_trusted_proxy_is_used(string peer, string key, string value)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { [key] = value }, AddFakePeer);
        var statuses = await LoginPostsAsync(factory, peer, 11);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses); // each forwarded client has its own bucket
    }

    // R2-ACC-6: a LAN or Docker-range peer is no proxy by default; its X-Forwarded-For cannot pick a fresh bucket per request.
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.17.0.1")]
    [InlineData("192.168.1.20")]
    [InlineData("fd00::5")]
    public async Task Forwarded_for_from_a_private_address_is_ignored_by_default(string peer)
    {
        await using var factory = new AppFactory(services: AddFakePeer);
        var statuses = await LoginPostsAsync(factory, peer, 11);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Fact]
    public void Default_trust_is_the_containers_own_networks_in_a_container_and_nothing_else()
    {
        IConfiguration Config(string? inContainer) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DOTNET_RUNNING_IN_CONTAINER"] = inContainer }).Build();
        Assert.Empty(ReverseProxy.DefaultNetworks(Config(null))); // not even loopback (R3-DEP-7)
        Assert.Empty(ReverseProxy.DefaultNetworks(Config("false")));
        Assert.DoesNotContain(ReverseProxy.DefaultNetworks(Config("true")), n => n.StartsWith("127.") || n == "::1/128");

        // The networks of a container's interfaces: bridge networks masked to their prefix; loopback and link-local left out.
        Assert.Equal(["172.18.0.0/16", "fd00:1::/64"], ReverseProxy.ContainerNetworks(
        [
            (IPAddress.Parse("127.0.0.1"), 8),
            (IPAddress.Parse("::1"), 128),
            (IPAddress.Parse("172.18.0.3"), 16),
            (IPAddress.Parse("fe80::42:acff:fe12:3"), 64),
            (IPAddress.Parse("fd00:1::3"), 64),
            (IPAddress.Parse("172.18.0.4"), 16),
        ]));
    }

    [Fact]
    public void R2_ACC_6_abuse_tracker_memory_is_bounded()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        var tracker = new AbuseTracker(clock, Options.Create(new AbuseOptions()));
        // A long-lived block survives the flood of fresh keys.
        for (var i = 0; i < 20; i++) tracker.Record("192.0.2.1", null, AbuseSignal.RateLimited);
        Assert.NotNull(tracker.Find(IPAddress.Parse("192.0.2.1"), null));
        for (var i = 0; i < 3 * AbuseTracker.MaxEntries; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(1);
            tracker.Record($"2001:db8:{i >> 16:x}:{i & 0xFFFF:x}::/48", null, AbuseSignal.FailedLogin);
        }
        Assert.InRange(tracker.TrackedCount, 1, AbuseTracker.MaxEntries);
        Assert.NotNull(tracker.Find(IPAddress.Parse("192.0.2.1"), null));
        // The most recent key is still counted.
        Assert.True(tracker.TrackedCount > AbuseTracker.MaxEntries / 2);
    }

    [Fact]
    public async Task KnownProxies_replaces_the_default_trust()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["ReverseProxy:KnownProxies"] = "192.0.2.10" }, AddFakePeer);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, await LoginPostsAsync(factory, "192.0.2.10", 11));
        // A private address is no longer trusted once an explicit list is configured.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginPostsAsync(factory, "10.0.0.5", 11))[10]);
    }

    // ---- A4: account enumeration --------------------------------------------------------------

    [Fact]
    public async Task Passkey_request_options_is_rate_limited()
    {
        await using var factory = new AppFactory();
        var client = factory.CreateBrowser();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await client.PostAsync("/Account/PasskeyRequestOptions?username=x%40example.com", new FormUrlEncodedContent([]))).StatusCode);
        }
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Fact]
    public async Task Login_with_unknown_email_costs_a_password_hash()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["RateLimiting:AuthPermitPerMinute"] = "100" });
        await CreateUserAsync(factory, "known@example.com");
        var browser = factory.CreateBrowser();
        await AppFactory.LoginAsync(browser, "warmup@example.com", "wrong password"); // JIT, first page render

        async Task<double> MedianMs(string email)
        {
            var times = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                var response = await AppFactory.LoginAsync(browser, email, "wrong password " + i);
                sw.Stop();
                Assert.Contains("Invalid login attempt", await response.Content.ReadAsStringAsync());
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            return times.Order().ElementAt(1);
        }

        var known = await MedianMs("known@example.com");
        var unknown = await MedianMs("nobody@example.com");
        // Both run one 600k-iteration PBKDF2; without the dummy hash "unknown" is several times faster.
        Assert.True(unknown > known * 0.5, $"unknown {unknown:0} ms vs known {known:0} ms");
    }

    // ---- A5: denial precedence ----------------------------------------------------------------

    [Fact]
    public async Task Unknown_invite_token_on_a_closed_poll_is_404()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));

        var response = await factory.CreateBrowser().GetAsync($"/p/{id}?t=" + InviteTokens.Generate().Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var access = await WithScope(factory, sp => sp.GetRequiredService<PollAccessService>()
            .GetAsync(id, new VisitorContext(null, null, "unknown-hash")));
        Assert.Equal(AccessDenial.InviteUnknown, access!.Denial);
        Assert.True(access.IsClosed);
    }

    // ---- A6: VoterKey is per poll ---------------------------------------------------------------

    [Fact]
    public async Task Voter_key_differs_per_poll_and_is_stable_across_restarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "telrang-tests-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["App:DataDir"] = dir };
        try
        {
            string id1, id2, voter1;
            var visitor = new VisitorContext(null, "raw-browser-key", null);
            await using (var factory = new AppFactory(settings))
            {
                var owner = await CreateUserAsync(factory, "o@example.com");
                id1 = await CreatePollAsync(factory, owner, RankedDraft());
                id2 = await CreatePollAsync(factory, owner, RankedDraft());
                var access = (string id) => WithScope(factory, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id, visitor));
                voter1 = (await access(id1))!.Voter!.VoterKey!;
                Assert.Equal(voter1, (await access(id1))!.Voter!.VoterKey);        // same vk, same poll: same key
                Assert.NotEqual(voter1, (await access(id2))!.Voter!.VoterKey);     // same vk, other poll: different key
                Assert.DoesNotContain("raw-browser-key", voter1);
                Assert.Equal(43, voter1.Length);                         // base64url of 32 bytes

                Assert.True((await SubmitRankedAsync(factory, id1, visitor, ParisFirst)).Succeeded);
                Assert.Equal(voter1, Assert.Single(await BallotsAsync(factory, id1)).VoterKey);
            }
            SqliteConnection.ClearAllPools();
            Assert.True(File.Exists(Path.Combine(dir, "keys", "voterkey")));

            await using (var restarted = new AppFactory(settings))
            {
                var again = await WithScope(restarted, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id1, visitor));
                Assert.Equal(voter1, again!.Voter!.VoterKey);
                Assert.True(again.HasBallot);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- A7: reviewer's missing tests ---------------------------------------------------------

    [Fact]
    public async Task Strangers_get_not_found_from_owner_operations()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var stranger = await CreateUserAsync(factory, "s@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var polls = (Func<IServiceProvider, PollService>)(sp => sp.GetRequiredService<PollService>());

        Assert.Null(await WithScope(factory, sp => polls(sp).GenerateInvitesAsync(stranger, id, 3, [])));
        Assert.Null(await WithScope(factory, sp => polls(sp).ListInvitesAsync(stranger, id)));
        Assert.Null(await WithScope(factory, sp => polls(sp).GetOverviewAsync(stranger, id)));
        Assert.Empty((await WithScope(factory, sp => polls(sp).ListInvitesAsync(owner, id)))!);
        Assert.NotNull(await WithScope(factory, sp => polls(sp).GetOverviewAsync(owner, id)));
    }

    [Theory]
    [InlineData("[x](javascript&#58;alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("![x](data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=)")]
    [InlineData("[x](data:text/html,<script>alert(1)</script>)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x]: javascript:alert(1)\n\n[link][x]")]
    public void Markdown_neutralises_dangerous_urls(string markdown)
    {
        var html = MarkdownRenderer.Render(markdown).Value;
        foreach (var attr in new[] { "href=\"", "src=\"" })
        {
            var at = 0;
            while ((at = html.IndexOf(attr, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                at += attr.Length;
                var value = html[at..html.IndexOf('"', at)];
                Assert.True(value == "#" || value.StartsWith("http", StringComparison.OrdinalIgnoreCase), $"{markdown} -> {html}");
            }
        }
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task After_vote_results_are_denied_to_a_browser_that_has_not_voted()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(results: ResultsVisibility.AfterVote));

        var (voter, voterJar) = factory.CreateBrowserWithJar();
        await voter.GetAsync($"/p/{id}");
        Assert.True((await SubmitRankedAsync(factory, id, VisitorFrom(factory, voterJar, id), ParisFirst)).Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await voter.GetAsync($"/p/{id}/results")).StatusCode);

        var (other, otherJar) = factory.CreateBrowserWithJar();
        await other.GetAsync($"/p/{id}");
        Assert.NotEqual(VisitorFrom(factory, voterJar, id).LinkVoterKey, VisitorFrom(factory, otherJar, id).LinkVoterKey);
        var denied = await other.GetAsync($"/p/{id}/results");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("results-table", await denied.Content.ReadAsStringAsync());
    }
}
