using System.Net;
using System.Text.RegularExpressions;
using Telrang.Web.Data;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §22.2: every admin option carries an <c>OptionHelp</c> description.</summary>
public partial class AdminHelpTests
{
    private static readonly string[] TabFiles =
        ["AdminUsers.razor", "AdminPolls.razor", "AdminSettings.razor", "AdminMail.razor", "AdminAbuse.razor", "AdminStatusTab.razor", "AdminLogTab.razor"];

    private static string AdminFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Telrang.Web", "Features", "Admin", name));
    }

    [GeneratedRegex("""<(label|legend)\b[^>]*>""")]
    private static partial Regex LabelTag();

    [GeneratedRegex("""\bfor="confirm-""")]
    private static partial Regex ConfirmTickBox();

    [GeneratedRegex("\\bdata-help=\"([^\"]+)\"")]
    private static partial Regex SharedHelp();

    [GeneratedRegex("""<OptionHelp\b[^>]*>""")]
    private static partial Regex OptionHelpTag();

    /// <summary>
    /// Each label (and legend) on an admin tab is followed by an <c>OptionHelp</c> before the next label, or names a shared
    /// description in the same file with <c>data-help</c>. The tick boxes of confirm panels state a consequence and are no
    /// option. Every tab has at least one description, and every <c>OptionHelp</c> names its label.
    /// </summary>
    [Fact]
    public void Every_labelled_admin_control_has_an_option_help()
    {
        var problems = new List<string>();
        foreach (var file in TabFiles)
        {
            var source = AdminFile(file);
            var helps = OptionHelpTag().Matches(source);
            if (helps.Count == 0) problems.Add($"{file}: no OptionHelp");
            foreach (Match help in helps)
            {
                if (!help.Value.Contains("Label=\"", StringComparison.Ordinal)) problems.Add($"{file}: OptionHelp without Label: {help.Value}");
            }

            var labels = LabelTag().Matches(source);
            for (var i = 0; i < labels.Count; i++)
            {
                var tag = labels[i].Value;
                if (ConfirmTickBox().IsMatch(tag)) continue;
                if (SharedHelp().Match(tag) is { Success: true } shared)
                {
                    if (!source.Contains($"<OptionHelp Id=\"{shared.Groups[1].Value}\"", StringComparison.Ordinal))
                        problems.Add($"{file}: {tag} names a missing OptionHelp");
                    continue;
                }
                var end = i + 1 < labels.Count ? labels[i + 1].Index : source.Length;
                var between = source[labels[i].Index..end];
                if (!between.Contains("<OptionHelp", StringComparison.Ordinal)) problems.Add($"{file}: {tag} has no OptionHelp");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Admin_tabs_render_folded_help_toggles_and_the_show_help_button()
    {
        await using var factory = new AppFactory();
        var id = await CreateUserAsync(factory, "admin@example.com");
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(id))!, AppRoles.Admin)).Succeeded);
            return 0;
        });
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");

        foreach (var tab in new[] { "users", "polls", "settings", "mail", "abuse", "status", "log" })
        {
            var response = await GetPageAsync(browser, "/admin?tab=" + tab);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("aria-pressed=\"false\"", html);
            Assert.Contains("Show help", html);
            Assert.Contains("class=\"option-help-toggle\" aria-expanded=\"false\" aria-controls=\"help-", html);
            Assert.Contains("aria-label=\"About ", html);
            Assert.Contains("class=\"option-help-panel\"", html);
            Assert.Matches("""class="option-help-panel" id="help-[^"]+" hidden""", html);
            Assert.DoesNotContain(" style=\"", html); // CSP: no inline styles
        }

        var settings = await (await GetPageAsync(browser, "/admin?tab=settings")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"About Public URL\"", settings);
        Assert.Contains("aria-label=\"About Auto-verify created accounts\"", settings);
        Assert.Contains("aria-label=\"About Ballots per poll\"", settings);
        // DESIGN.md §27: the options limit's help names the cap of dates polls.
        Assert.Contains("a dates poll has at most 128 whatever this is set to", System.Net.WebUtility.HtmlDecode(settings));
    }
}
