using System.Net;
using System.Net.Sockets;
using System.Text;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Request body and form limits for every request, and the abuse signals for refused forms (DESIGN.md §31).</summary>
public class RequestLimitTests
{
    private static HttpClient Peer(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Remove(PeerHeader);
        client.DefaultRequestHeaders.Add(PeerHeader, ip);
        return client;
    }

    /// <summary>A body that announces a length and never sends a byte until the test ends.</summary>
    private sealed class UnsentContent(long announced) : HttpContent
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Written;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await Release.Task;
            Interlocked.Increment(ref Written);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = announced;
            return true;
        }
    }

    /// <summary>A body of <paramref name="bytes"/> bytes sent without a Content-Length (chunked).</summary>
    private sealed class ChunkedContent(int bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = Encoding.ASCII.GetBytes("a=" + new string('b', 8190) + "&");
            for (var sent = 0; sent < bytes; sent += chunk.Length) await stream.WriteAsync(chunk);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task An_oversized_anonymous_post_is_413_before_the_body_is_read()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Abuse:Threshold"] = "4" }, AddFakePeer);
        var anon = Peer(factory.CreateBrowser(), "198.51.100.20");
        using var content = new UnsentContent(28_000_000);
        content.Headers.ContentType = new("application/x-www-form-urlencoded");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var refused = await anon.PostAsync("/", content, timeout.Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Contains("Form too large", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, Interlocked.Read(ref content.Written));
        content.Release.TrySetResult();

        // Without a Content-Length the body is counted as it is read.
        var chunked = new ChunkedContent(RequestLimits.MaxBodyBytes + 16 * 1024);
        chunked.Headers.ContentType = new("application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await anon.PostAsync("/Account/Logout", chunked)).StatusCode);

        // Two refusals (weight 2 each) reach the threshold: the client is blocked, for that reason.
        var abuse = factory.Services.GetRequiredService<AbuseTracker>();
        Assert.Equal(nameof(AbuseSignal.FormTooLarge), abuse.Find(IPAddress.Parse("198.51.100.20"), null)!.Reason);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.GetAsync("/")).StatusCode);
        Assert.Null(abuse.Find(IPAddress.Parse("198.51.100.21"), null));
    }

    [Fact]
    public async Task Too_many_or_too_long_form_values_are_400_with_a_signal()
    {
        await using var factory = new AppFactory(services: AddFakePeer);
        var anon = Peer(factory.CreateBrowser(), "198.51.100.30");
        var html = await (await anon.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = AppFactory.ScrapeAntiforgeryToken(html);

        var many = Enumerable.Range(0, RequestLimits.MaxFormValues + 1).ToDictionary(i => $"x{i}", _ => "1");
        many["__RequestVerificationToken"] = token;
        var tooMany = await anon.PostAsync("/Account/Login", new FormUrlEncodedContent(many));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("Form too large", await tooMany.Content.ReadAsStringAsync());

        var longValue = await anon.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = new string('a', RequestLimits.MaxValueLength + 1),
        }));
        Assert.Equal(HttpStatusCode.BadRequest, longValue.StatusCode);

        var longKey = await anon.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            [new string('k', RequestLimits.MaxKeyLength + 1)] = "1",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, longKey.StatusCode);

        // The account forms themselves still work: a normal sign-in.
        await CreateUserAsync(factory, "u@example.com");
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(Peer(factory.CreateBrowser(), "198.51.100.31"), "u@example.com", Password)).StatusCode);
    }

    [Fact]
    public async Task A_failed_antiforgery_check_is_a_signal_except_on_vote_posts()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Abuse:Threshold"] = "3" }, AddFakePeer);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var abuse = factory.Services.GetRequiredService<AbuseTracker>();
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "vote" });

        var voter = Peer(factory.CreateBrowser(), "198.51.100.40");
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.BadRequest, (await voter.PostAsync($"/p/{id}", form)).StatusCode);
        Assert.Null(abuse.Find(IPAddress.Parse("198.51.100.40"), null));

        var other = Peer(factory.CreateBrowser(), "198.51.100.41");
        for (var i = 0; i < 3; i++)
        {
            var expired = await other.PostAsync("/Account/Login", form);
            Assert.Contains("Form expired", await expired.Content.ReadAsStringAsync());
        }
        Assert.Equal(nameof(AbuseSignal.Antiforgery), abuse.Find(IPAddress.Parse("198.51.100.41"), null)!.Reason);
    }

    [Fact]
    public async Task The_blazor_hub_gets_room_for_long_polling_circuit_messages()
    {
        // A long-polling send carries whole circuit messages: the largest fits, several of them too.
        Assert.True(RequestLimits.HubBodyBytes >= 2L * LimitSettings.MaxCircuitMessageBytes);
        Assert.True(RequestLimits.MaxBodyBytes < LimitSettings.MaxCircuitMessageBytes); // so the hub needs its own limit

        // The middleware raises the server's limit for /_blazor and leaves it for everything else.
        foreach (var (path, expected) in new[] { ("/_blazor", (long?)RequestLimits.HubBodyBytes), ("/", (long?)RequestLimits.MaxBodyBytes) })
        {
            var http = new DefaultHttpContext();
            var size = new BodySize { MaxRequestBodySize = RequestLimits.MaxBodyBytes };
            http.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
            http.Request.Method = "POST";
            http.Request.Path = path;
            http.Request.QueryString = new QueryString("?id=abc");
            var ran = false;
            await new FormLimitMiddleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(http);
            Assert.True(ran);
            Assert.Equal(expected, size.MaxRequestBodySize);
        }

        await using var factory = new AppFactory();
        var client = factory.CreateBrowser();
        var circuitSized = new ByteArrayContent(new byte[LimitSettings.MaxCircuitMessageBytes]);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/_blazor?id=unknown", circuitSized)).StatusCode);
        var tooLarge = new ByteArrayContent(new byte[RequestLimits.HubBodyBytes + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/_blazor?id=unknown", tooLarge)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PostAsync("/", new ByteArrayContent(new byte[LimitSettings.MaxCircuitMessageBytes]))).StatusCode);
    }

    private sealed class BodySize : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    [Fact]
    public async Task Other_posts_have_a_per_client_rate_limit_without_a_signal()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["RateLimiting:OtherPostsPerMinute"] = "3" }, AddFakePeer);
        var anon = Peer(factory.CreateBrowser(), "2001:db8:5:1::1");
        var empty = new FormUrlEncodedContent(new Dictionary<string, string>());
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, (await anon.PostAsync("/", empty)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.PostAsync("/anything", empty)).StatusCode);
        // One /48 is one client; another client and the Blazor hub are not limited by it.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:5:2::1").PostAsync("/", empty)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:6::1").PostAsync("/", empty)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await anon.PostAsync("/_blazor?id=unknown", empty)).StatusCode);
        // Pages still load, and the refusals block nobody.
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/")).StatusCode);
        Assert.Null(factory.Services.GetRequiredService<AbuseTracker>().Find(IPAddress.Parse("2001:db8:5:1::1"), null));
    }

    [Fact]
    public async Task Kestrel_refuses_an_oversized_body_from_its_headers()
    {
        var port = AppProcess.FreePort();
        var env = new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" };
        await AppProcess.RunWhileHealthyAsync(env, $"http://127.0.0.1:{port}", async () =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            // Headers only: the 28 MB body is never sent, so a 413 means nothing waited for it.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: 28000000\r\n\r\n"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            Assert.StartsWith("HTTP/1.1 413", Encoding.ASCII.GetString(buffer, 0, read));

            using var http = new HttpClient();
            var page = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
            Assert.False(page.Headers.Contains("Server"));
        });
    }

    [Fact]
    public async Task The_sign_in_limit_counts_an_ipv6_48_as_one_client()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:AuthPermitPerMinute"] = "2",
            ["RateLimiting:SetupPermitPerMinute"] = "1",
        }, AddFakePeer);
        var empty = new FormUrlEncodedContent(new Dictionary<string, string>());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:7:1::5").PostAsync("/Account/Login", empty)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:7:2::5").PostAsync("/Account/ForgotPassword", empty)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:7:3::5").PostAsync("/Account/Login", empty)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:8:1::5").PostAsync("/Account/Login", empty)).StatusCode);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:9:1::5").PostAsync("/setup", empty)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Peer(factory.CreateBrowser(), "2001:db8:9:2::5").PostAsync("/setup", empty)).StatusCode);
    }
}
