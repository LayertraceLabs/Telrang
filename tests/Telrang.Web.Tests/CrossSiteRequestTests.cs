using System.Net;
using Microsoft.AspNetCore.Http;
using Telrang.Web.Security;

namespace Telrang.Web.Tests;

/// <summary>Requests another site makes a browser send are refused before any limit or abuse signal (DESIGN.md §31).</summary>
public class CrossSiteRequestTests
{
    private static HttpRequest Request(string method, params (string Name, string Value)[] headers)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("vote.example.com");
        foreach (var (name, value) in headers) http.Request.Headers[name] = value;
        return http.Request;
    }

    [Theory]
    // A browser with Fetch Metadata.
    [InlineData("POST", "same-origin", "navigate", "document", null, false)]
    [InlineData("POST", "none", "navigate", "document", null, false)]
    [InlineData("POST", "cross-site", "navigate", "document", null, true)]
    [InlineData("POST", "cross-site", "no-cors", "empty", null, true)]
    [InlineData("POST", "same-site", "navigate", "document", null, true)]
    [InlineData("GET", "cross-site", "navigate", "document", null, false)] // a link from elsewhere
    [InlineData("GET", "same-site", "navigate", "document", null, false)]
    [InlineData("GET", "cross-site", "no-cors", "image", null, true)]
    [InlineData("GET", "cross-site", "cors", "empty", null, true)]
    [InlineData("GET", "cross-site", "navigate", "iframe", null, true)]
    [InlineData("GET", "cross-site", "navigate", "embed", null, true)]
    [InlineData("GET", "same-origin", "cors", "empty", null, false)]
    [InlineData("GET", "same-origin", "websocket", "empty", null, false)]
    [InlineData("GET", "weird", "navigate", "document", null, false)]
    [InlineData("POST", "weird", "navigate", "document", null, true)]
    // Without Fetch Metadata: Origin decides, on unsafe methods only.
    [InlineData("POST", null, null, null, null, false)] // curl
    [InlineData("POST", null, null, null, "https://vote.example.com", false)]
    [InlineData("POST", null, null, null, "HTTPS://VOTE.EXAMPLE.COM", false)]
    [InlineData("POST", null, null, null, "https://evil.example", true)]
    [InlineData("POST", null, null, null, "http://vote.example.com", false)] // scheme not compared: a proxy may hide it
    [InlineData("POST", null, null, null, "https://public.example.org", false)] // the Public URL
    [InlineData("POST", null, null, null, "https://vote.example.com.evil.example", true)]
    [InlineData("POST", null, null, null, "null", true)]
    [InlineData("DELETE", null, null, null, "https://evil.example", true)]
    [InlineData("GET", null, null, null, "https://evil.example", false)]
    public void The_rules(string method, string? site, string? mode, string? dest, string? origin, bool refused)
    {
        var headers = new List<(string, string)>();
        if (site is not null) headers.Add(("Sec-Fetch-Site", site));
        if (mode is not null) headers.Add(("Sec-Fetch-Mode", mode));
        if (dest is not null) headers.Add(("Sec-Fetch-Dest", dest));
        if (origin is not null) headers.Add(("Origin", origin));
        Assert.Equal(refused, CrossSiteRequests.IsRefused(Request(method, [.. headers]), "https://public.example.org"));
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent([]);
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        return (await client.SendAsync(request)).StatusCode;
    }

    // Pentest round 4: on .NET 11 these were a 500 (the form read after the framework's cross-origin verdict), and
    // 31 of them from any web page got the visitor's address blocked.
    [Fact]
    public async Task Cross_site_posts_are_403_and_spend_no_limit_and_raise_no_block()
    {
        await using var factory = new AppFactory();
        var client = factory.CreateBrowser();
        for (var i = 0; i < 60; i++)
        {
            var headers = i % 2 == 0
                ? new[] { ("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "no-cors") }
                : [("Origin", "https://evil.example")];
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(client, HttpMethod.Post, "/Account/Login", headers));
        }
        // The visitor's own request afterwards: neither limited (429) nor blocked, just a form without a token.
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, HttpMethod.Post, "/Account/Login"));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/Account/Login"));
    }

    [Fact]
    public async Task Cross_site_subresource_gets_are_403_links_and_assets_pass()
    {
        await using var factory = new AppFactory();
        var client = factory.CreateBrowser();
        (string, string)[] image = [("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "no-cors"), ("Sec-Fetch-Dest", "image")];
        for (var i = 0; i < 150; i++) // more than any page-GET limit
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(client, HttpMethod.Get, "/Account/Login", image));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/Account/Login",
            ("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "document")));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(client, HttpMethod.Get, "/Account/Login",
            ("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "iframe")));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/healthz", image));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/branding/theme.css", image));
    }
}
