using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §23.5: a new poll's time zone is the account's, else the browser's, else UTC.</summary>
public class BrowserTimeZoneTests
{
    [Theory]
    [InlineData("Europe/Amsterdam", "UTC", "UTC", "Europe/Amsterdam")] // blank form: the browser's zone
    [InlineData("Europe/Amsterdam", "Asia/Tokyo", "UTC", null)] // the form already holds another zone (a restored draft)
    [InlineData("Europe/Amsterdam", "", "UTC", null)] // emptied field: not the server default
    [InlineData("UTC", "UTC", "UTC", null)] // nothing to change
    [InlineData("Mars/Olympus_Mons", "UTC", "UTC", null)] // unknown id
    [InlineData("W. Europe Standard Time", "UTC", "UTC", null)] // Windows id: not IANA
    [InlineData(null, "UTC", "UTC", null)] // the browser gave none
    public void Browser_zone_applies_only_over_the_server_default(string? browser, string? field, string? serverDefault, string? expected) =>
        Assert.Equal(expected, PollEditor.BrowserZoneToApply(browser, field, serverDefault));

    [Fact]
    public async Task New_poll_starts_with_the_account_zone_and_asks_the_browser_only_without_one()
    {
        await using var factory = new AppFactory();
        var id = await CreateUserAsync(factory, "o@example.com");
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        var html = await (await GetPageAsync(browser, "/polls/new")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"UTC\"", ZoneInput(html));

        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            user.TimeZone = "Europe/Berlin";
            Assert.True((await users.UpdateAsync(user)).Succeeded);
            return 0;
        });
        html = await (await GetPageAsync(browser, "/polls/new")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"Europe/Berlin\"", ZoneInput(html));

        var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "Telrang.Web", "wwwroot", "js", "timezone.js"));
        Assert.Contains("export function browserZone()", source);
    }

    private static string ZoneInput(string html)
    {
        var tag = System.Text.RegularExpressions.Regex.Match(html, "<input id=\"[^\"]*-timezone\"[^>]*>").Value;
        Assert.NotEmpty(tag);
        return tag;
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
