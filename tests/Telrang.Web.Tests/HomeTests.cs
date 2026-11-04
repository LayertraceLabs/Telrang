using System.Net;
using System.Text.RegularExpressions;
using Telrang.Web.Features.Settings;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>The home page with its examples (DESIGN.md §23.2).</summary>
public partial class HomeTests
{
    private static readonly string[] Titles =
    [
        "Where do we eat on Friday?",
        "Split 100 points over next year&#x27;s club trips",
        "When can you come to the board games night?",
    ];

    private static async Task<string> HomeAsync(HttpClient client)
    {
        var response = await GetPageAsync(client, "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The showcase section of the page.</summary>
    private static string Showcase(string html)
    {
        var start = html.IndexOf("<section class=\"showcase\"", StringComparison.Ordinal);
        Assert.True(start > 0, "no showcase");
        return html[start..html.IndexOf("</main>", start, StringComparison.Ordinal)]; // the showcase ends the page
    }

    [GeneratedRegex("data-ballot>(.*?)showcase-part-title\">Results", RegexOptions.Singleline)]
    private static partial Regex BallotPartRegex();

    private static void AssertStaticShowcase(string html, string shell = "shell-public")
    {
        Assert.DoesNotContain("<!--Blazor:", html); // no interactive component: no circuit
        Assert.Contains("_framework/blazor.web.js", html); // still there for enhanced navigation
        Assert.Contains("js/showcase.js", html);
        Assert.Contains($"class=\"shell {shell}\"", html);
        Assert.DoesNotContain(" style=\"", html);

        var showcase = Showcase(html);
        Assert.DoesNotContain("<form", showcase);
        Assert.DoesNotContain("__RequestVerificationToken", showcase);
        Assert.DoesNotContain("_fts", showcase);
        Assert.DoesNotContain("type=\"submit\"", showcase);
        foreach (var title in Titles) Assert.Contains(title, showcase);
        Assert.Equal(3, Regex.Matches(showcase, "This example is <em>interactive</em>; nothing is sent or saved.").Count);

        // The voting fields of all three examples, with no name: nothing could be posted even inside a form.
        var ballots = BallotPartRegex().Matches(showcase).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(3, ballots.Count);
        Assert.Contains("data-ranked", ballots[0]);
        Assert.Contains("data-points", ballots[1]);
        Assert.Contains("data-dates-vote", ballots[2]);
        Assert.All(ballots, b => Assert.DoesNotContain("name=", b));
        Assert.All(ballots, b => Assert.Contains("data-field=\"o.", b));

        // Results for the 7 made-up ballots, scored at render time.
        Assert.Equal(3, Regex.Matches(showcase, "7 voters").Count);
        Assert.Contains("Thai kitchen", showcase);
        Assert.Contains("Best: ", showcase);

        Assert.DoesNotContain("Dutch <em>tel</em>", html); // the name is explained in the README
    }

    [Fact]
    public async Task Home_is_static_and_shows_the_examples_to_anonymous_visitors()
    {
        await using var factory = new AppFactory();
        var html = await HomeAsync(factory.CreateBrowser());

        AssertStaticShowcase(html);
        Assert.Contains("Ranked choice, points and date polls, shared by link.", html);
        Assert.Contains("href=\"Account/Login\"", html);
        Assert.Contains("href=\"Account/Register\"", html);
        Assert.DoesNotContain(">My polls<", html);
    }

    [Fact]
    public async Task Home_is_static_for_signed_in_users_with_my_polls_instead_of_log_in()
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "alice@example.com");
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.ThemeLayout] = "sidebar" }, null);
        var html = await HomeAsync(await LoggedInBrowserAsync(factory, "alice@example.com"));

        // §24.1: the signed-in shell (here the sidebar) with the app navigation, still without a circuit.
        AssertStaticShowcase(html, "shell-sidebar");
        Assert.Contains("class=\"app-nav ", html);
        Assert.Contains("<a class=\"btn btn-primary btn-lg\" href=\"polls\">My polls</a>", html);
        Assert.Contains("class=\"user-block\"", html);
        Assert.DoesNotContain("href=\"Account/Login", html);
        Assert.DoesNotContain("href=\"Account/Register\"", html);
    }
}
