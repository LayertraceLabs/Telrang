using System.Text.RegularExpressions;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>AV-15f: browser drafts do not survive signing out (wwwroot/js/draft.js, loaded on every page).</summary>
public class DraftSignOutTests
{
    [Fact]
    public async Task Every_page_loads_the_draft_module_next_to_the_logout_form()
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "o@example.com");
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        foreach (var path in new[] { "/polls", "/Account/Manage" })
        {
            var html = await (await GetPageAsync(browser, path)).Content.ReadAsStringAsync();
            Assert.Matches(new Regex("<script src=\"js/draft(\\.[a-z0-9]+)?\\.js\" type=\"module\"></script>"), html);
            Assert.Contains("action=\"Account/Logout\"", html);
            Assert.DoesNotContain("<script>", html); // still no inline script
        }

        var js = await (await browser.GetAsync("/js/draft.js")).Content.ReadAsStringAsync();
        Assert.Contains("'/account/logout'", js);
        Assert.Contains("dropDrafts(undefined)", js);
        Assert.Contains("dropDrafts(userOf(key))", js);
    }
}
