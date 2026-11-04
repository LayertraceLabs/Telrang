using System.Net;
using System.Text.RegularExpressions;
using Telrang.Web.Components.Layout;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Links from interactive pages to static pages are plain page loads (DESIGN.md §24.6).</summary>
public class StaticRoutesTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/p/AAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("/p/AAAAAAAAAAAAAAAAAAAAAA/results", true)]
    [InlineData("/p/AAAAAAAAAAAAAAAAAAAAAA/", true)]
    [InlineData("/Account/Login", true)]
    [InlineData("/account/manage", true)]
    [InlineData("/Account/Manage/Email", true)]
    [InlineData("/setup", true)]
    [InlineData("/polls", false)]
    [InlineData("/polls/new", false)]
    [InlineData("/polls/AAAAAAAAAAAAAAAAAAAAAA/share", false)]
    [InlineData("/my/votes", false)]
    [InlineData("/admin", false)]
    [InlineData("/p/AAAAAAAAAAAAAAAAAAAAAA/results/live", false)] // an endpoint, not a page
    [InlineData("/pp", false)]
    public void The_pattern_matches_exactly_the_static_pages(string path, bool expected)
    {
        // The same syntax in .NET and JavaScript: groups, classes and escapes only.
        var regex = new Regex(StaticRoutes.Pattern, RegexOptions.IgnoreCase);
        Assert.Equal(expected, regex.IsMatch(path));
    }

    [Fact]
    public async Task Interactive_pages_carry_the_routes_and_static_pages_do_not()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");

        foreach (var (path, interactive) in new[] { ("/polls", true), ("/my/votes", true), ($"/polls/{id}/share", true), ("/", false), ($"/p/{id}", false), ($"/p/{id}/results", false), ("/Account/Manage", false) })
        {
            var response = await GetPageAsync(browser, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("js/links", html); // the module is on every page; without the meta it does nothing
            Assert.Equal(interactive, html.Contains("<meta name=\"av-static-routes\"", StringComparison.Ordinal));
            if (interactive) Assert.Contains($"content=\"{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(StaticRoutes.Pattern)}\"", html);
        }
    }
}
