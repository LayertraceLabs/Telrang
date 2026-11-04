using System.Net;
using System.Text.RegularExpressions;
using Telrang.Core.Polls;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §23.4: the poll editor's controls carry an <c>OptionHelp</c>, with the same fold-all button as admin.</summary>
public partial class EditorHelpTests
{
    private static string WebFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Telrang.Web", .. parts]));
    }

    /// <summary>Field labels of the editor: <c>for="@Id(...)"</c> (titles, settings, close date, zone).</summary>
    [GeneratedRegex("""<label\b[^>]*\bfor="@Id\(([^)]*)\)"[^>]*>([^<]*)<""")]
    private static partial Regex FieldLabel();

    [GeneratedRegex("""<label\b""")]
    private static partial Regex AnyLabel();

    [GeneratedRegex("""@Radios\("(\w+)", "([^"]+)", (\w+)\(\)""")]
    private static partial Regex RadiosCall();

    [GeneratedRegex("""@Radios\(""")]
    private static partial Regex AnyRadiosCall();

    private static readonly string[] Covered =
        ["Poll type", "Scoring", "Bans per voter", "Rank all", "Budget", "Cap per option", "Spend all", "Allow maybe", "Closes on",
         "Time zone", "Text format", "Show ballots to voters", "Calendar"];

    private static readonly string[] RadioGroups = ["Who votes", "Responses", "Names", "Results for", "Show results"];

    /// <summary>
    /// Every field label in the editor except Title and Description is followed by an option help (an <c>OptionHelp</c> or
    /// one of the shared <c>...Help()</c> fragments) before the next label; every radio group passes its help; each control
    /// §23.4 lists has a description; Title and Description have none.
    /// </summary>
    [Fact]
    public void Every_labelled_editor_control_has_an_option_help()
    {
        var source = WebFile("Features", "Polls", "PollEditor.razor");
        var problems = new List<string>();

        var labels = AnyLabel().Matches(source).Select(m => m.Index).ToList();
        Assert.True(FieldLabel().Matches(source).Count >= 11, "the field label scan no longer matches the editor");
        foreach (Match label in FieldLabel().Matches(source))
        {
            var text = label.Groups[2].Value.Trim();
            if (text is "Title" or "Description") continue;
            var next = labels.FirstOrDefault(i => i > label.Index, source.Length);
            var between = source[label.Index..next];
            if (!between.Contains("<OptionHelp", StringComparison.Ordinal) && !Regex.IsMatch(between, @"@\w+Help\(\)"))
                problems.Add($"label {text} has no OptionHelp");
        }

        var radios = RadiosCall().Matches(source).Select(m => m.Groups[2].Value).ToList();
        if (radios.Count != AnyRadiosCall().Matches(source).Count) problems.Add("a radio group without a help fragment");
        foreach (var group in RadioGroups)
        {
            if (!radios.Contains(group)) problems.Add($"radio group {group} has no help");
        }
        if (!source.Contains("<OptionHelp Label=\"@legend\">@help</OptionHelp>", StringComparison.Ordinal)) problems.Add("Radios does not render its help");

        foreach (var label in Covered)
        {
            if (!source.Contains($"<OptionHelp Label=\"{label}\"", StringComparison.Ordinal)) problems.Add($"{label}: no OptionHelp");
        }
        foreach (var label in new[] { "Title", "Description" })
        {
            if (source.Contains($"<OptionHelp Label=\"{label}\"", StringComparison.Ordinal)) problems.Add($"{label} has help (§23.4: none)");
        }
        if (!source.Contains("<HelpAllButton State=\"help\"", StringComparison.Ordinal)) problems.Add("no Show help button");
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var share = WebFile("Features", "Polls", "SharePoll.razor");
        Assert.Contains("<OptionHelp Label=\"How many\">", share);
        Assert.Contains("<OptionHelp Label=\"Aliases\">", share);
    }

    [Fact]
    public async Task Editor_and_share_page_render_folded_help()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var points = await CreatePollAsync(factory, owner, PointsDraft(voters: VoterMode.Invite));
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        var response = await GetPageAsync(browser, "/polls/new");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("aria-pressed=\"false\"", html);
        Assert.Contains("Show help", html);
        Assert.Matches("""class="option-help-panel" id="help-[^"]+" hidden""", html);
        foreach (var label in new[] { "Poll type", "Scoring", "Bans per voter", "Rank all", "Who votes", "Responses", "Names", "Results for", "Show results", "Show ballots to voters", "Closes on", "Time zone" })
            Assert.Contains($"aria-label=\"About {label}\"", html);
        Assert.DoesNotContain("aria-label=\"About Title\"", html);
        Assert.DoesNotContain("aria-label=\"About Description\"", html);
        Assert.DoesNotContain(" style=\"", html); // CSP: no inline styles

        var edit = await (await GetPageAsync(browser, $"/polls/{points}/edit")).Content.ReadAsStringAsync();
        foreach (var label in new[] { "Poll type", "Budget", "Cap per option", "Spend all" })
            Assert.Contains($"aria-label=\"About {label}\"", edit);

        var share = await (await GetPageAsync(browser, $"/polls/{points}/share")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"About How many\"", share);
        Assert.Contains("aria-label=\"About Aliases\"", share);
    }
}
