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

    /// <summary>The poll's own controls (PollEditor.razor): voting, results, closing, the outline's pages and groups.</summary>
    private static readonly string[] Covered =
        ["Closes on", "Time zone", "Show ballots to voters", "Questions, groups and pages", "Page title", "Page description", "Group title",
         "Group description"];

    /// <summary>A question's controls (QuestionEditor.razor, DESIGN.md §23.4, §34.5): every type's settings, written once.</summary>
    private static readonly string[] QuestionCovered =
        ["@TypeLabel", "Required", "Question", "Help text", "Scoring", "Bans per voter", "Rank all", "Budget", "Cap per option", "Spend all",
         "Allow maybe", "Fewest picks", "Most picks", "Other, with text", "Shown as", "Multiple lines", "Shortest answer", "Longest answer",
         "Smallest", "Largest", "Decimal places", "Unit", "Text format", "Calendar"];

    private static readonly string[] RadioGroups = ["Who votes", "Responses", "Names", "Results for", "Show results"];

    /// <summary>
    /// Every field label in the editor except Title and Description is followed by an option help (an <c>OptionHelp</c> or
    /// one of the shared <c>...Help()</c> fragments) before the next label; every radio group passes its help; each control
    /// §23.4 and §34.5 list has a description; Title and Description have none.
    /// </summary>
    [Fact]
    public void Every_labelled_editor_control_has_an_option_help()
    {
        var problems = new List<string>();
        foreach (var (file, covered, minimum) in new[] { ("PollEditor.razor", Covered, 6), ("QuestionEditor.razor", QuestionCovered, 18) })
        {
            var source = WebFile("Features", "Polls", file);
            var labels = AnyLabel().Matches(source).Select(m => m.Index).ToList();
            Assert.True(FieldLabel().Matches(source).Count >= minimum, $"the field label scan no longer matches {file}");
            foreach (Match label in FieldLabel().Matches(source))
            {
                var text = label.Groups[2].Value.Trim();
                if (text is "Title" or "Description") continue;
                var next = labels.FirstOrDefault(i => i > label.Index, source.Length);
                var between = source[label.Index..next];
                if (!between.Contains("<OptionHelp", StringComparison.Ordinal) && !Regex.IsMatch(between, @"@\w+Help\(\)"))
                    problems.Add($"{file}: label {text} has no OptionHelp");
            }
            foreach (var label in covered)
            {
                if (!source.Contains($"<OptionHelp Label=\"{label}\"", StringComparison.Ordinal)) problems.Add($"{file}: {label}: no OptionHelp");
            }
            foreach (var label in new[] { "Title", "Description" })
            {
                if (source.Contains($"<OptionHelp Label=\"{label}\"", StringComparison.Ordinal)) problems.Add($"{label} has help (§23.4: none)");
            }
        }

        var editor = WebFile("Features", "Polls", "PollEditor.razor");
        var radios = RadiosCall().Matches(editor).Select(m => m.Groups[2].Value).ToList();
        if (radios.Count != AnyRadiosCall().Matches(editor).Count) problems.Add("a radio group without a help fragment");
        foreach (var group in RadioGroups)
        {
            if (!radios.Contains(group)) problems.Add($"radio group {group} has no help");
        }
        if (!editor.Contains("<OptionHelp Label=\"@legend\">@help</OptionHelp>", StringComparison.Ordinal)) problems.Add("Radios does not render its help");
        if (!editor.Contains("<HelpAllButton State=\"help\"", StringComparison.Ordinal)) problems.Add("no Show help button");
        // One QuestionEditor (DESIGN.md §34.5): no type's settings in the poll form itself.
        foreach (var setting in new[] { "Bans per voter", "Budget", "Fewest picks", "Decimal places", "Longest answer" })
        {
            if (editor.Contains(setting, StringComparison.Ordinal)) problems.Add($"PollEditor.razor holds {setting}: type settings belong in QuestionEditor");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var share = WebFile("Features", "Polls", "SharePoll.razor");
        Assert.Contains("<OptionHelp Label=\"How many\">", share);
        Assert.Contains("<OptionHelp Label=\"Aliases\">", share);
    }

    /// <summary>
    /// The look controls (DESIGN.md §34.6, §34.7): the editor's Look tab and section (<c>PollLookEditor</c>) and the controls it
    /// shares with the admin's theme section (<c>LookFields</c>): every label has an option help, or names the shared one.
    /// </summary>
    [Fact]
    public void Every_look_control_has_an_option_help()
    {
        var problems = new List<string>();
        foreach (var (file, covered) in new[]
                 {
                     (WebFile("Features", "Looks", "PollLookEditor.razor"), new[] { "Look", "Look name" }),
                     (WebFile("Features", "Branding", "LookFields.razor"), new[] { "Font", "Corner radius", "Colours" }),
                 })
        {
            var labels = AnyLabel().Matches(file).Select(m => m.Index).ToList();
            Assert.NotEmpty(labels);
            foreach (var at in labels)
            {
                var next = labels.FirstOrDefault(i => i > at, file.Length);
                var between = file[at..next];
                var tag = between[..(between.IndexOf('>') + 1)];
                if (!between.Contains("<OptionHelp", StringComparison.Ordinal) && !tag.Contains("data-help=", StringComparison.Ordinal))
                    problems.Add($"{tag} has no OptionHelp");
            }
            foreach (var label in covered)
            {
                if (!Regex.IsMatch(file, "<OptionHelp\\b[^>]*\\bLabel=\"" + Regex.Escape(label) + "\"")) problems.Add($"{label}: no OptionHelp");
            }
        }
        // The admin's theme section uses the same controls (no second set of look controls).
        Assert.Contains("<LookFields Form=\"look\" Instance=\"true\"", WebFile("Features", "Admin", "AdminSettings.razor"));
        Assert.DoesNotContain("type=\"color\"", WebFile("Features", "Admin", "AdminSettings.razor"));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The numbers in the new types' help (DESIGN.md §23.4: examples are pinned by a test) hold for the validators.</summary>
    [Fact]
    public void The_help_examples_of_the_new_types_hold()
    {
        var source = WebFile("Features", "Polls", "QuestionEditor.razor");
        Assert.Contains("with 2,\n                12.25 is accepted and 12.255 refused", source);
        var number = new PollQuestion("q1", PollType.Number, []) { Decimals = 2 };
        Assert.Empty(Telrang.Core.Ballots.BallotValidator.ValidateNumber(number, new Telrang.Core.Ballots.NumberBallot(12.25m)));
        Assert.NotEmpty(Telrang.Core.Ballots.BallotValidator.ValidateNumber(number, new Telrang.Core.Ballots.NumberBallot(12.255m)));

        Assert.Contains("With fewest 2 and most 3, a\n                voter picks 2 or 3", source);
        var options = new[] { "a", "b", "c", "d" }.Select(k => new PollOption(k, k.ToUpperInvariant(), "")).ToList();
        var choice = new PollQuestion("q1", PollType.Choice, options) { MinSelect = 2, MaxSelect = 3 };
        IReadOnlyList<string> Picks(params string[] keys) => Telrang.Core.Ballots.BallotValidator.Validate(choice, new Telrang.Core.Ballots.ChoiceBallot(keys));
        Assert.NotEmpty(Picks("a"));
        Assert.Empty(Picks("a", "b"));
        Assert.Empty(Picks("a", "b", "c"));
        Assert.NotEmpty(Picks("a", "b", "c", "d"));

        Assert.Contains("limit of one quadrillion", source);
        Assert.Equal(1_000_000_000_000_000m, Telrang.Core.Polls.PollRules.NumberAbsMax);
        Assert.Contains("Polls.PollRules.MaxDecimals", source);
        Assert.Equal(6, Telrang.Core.Polls.PollRules.MaxDecimals);
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

        // The new types' fixed rules on edit (DESIGN.md §34.5), each with its help.
        var choice = await CreatePollAsync(factory, owner, new PollDraft
        {
            Title = "Lunch",
            Questions = [new QuestionDraft { Type = PollType.Choice, MaxSelect = 2, AllowOther = true, Options = [new() { Label = "A" }, new() { Label = "B" }] }],
        });
        var choiceEdit = await (await GetPageAsync(browser, $"/polls/{choice}/edit")).Content.ReadAsStringAsync();
        foreach (var label in new[] { "Poll type", "Required", "Fewest picks", "Most picks", "Other, with text" })
            Assert.Contains($"aria-label=\"About {label}\"", choiceEdit);
        var number = await CreatePollAsync(factory, owner, new PollDraft { Title = "Age", Questions = [new QuestionDraft { Type = PollType.Number, Unit = "years" }] });
        var numberEdit = await (await GetPageAsync(browser, $"/polls/{number}/edit")).Content.ReadAsStringAsync();
        foreach (var label in new[] { "Smallest", "Largest", "Decimal places", "Unit" })
            Assert.Contains($"aria-label=\"About {label}\"", numberEdit);
        var text = await CreatePollAsync(factory, owner, new PollDraft { Title = "Notes", Questions = [new QuestionDraft { Type = PollType.Text }] });
        var textEdit = await (await GetPageAsync(browser, $"/polls/{text}/edit")).Content.ReadAsStringAsync();
        foreach (var label in new[] { "Multiple lines", "Shortest answer", "Longest answer" })
            Assert.Contains($"aria-label=\"About {label}\"", textEdit);
        Assert.Contains("aria-label=\"About Questions, groups and pages\"", textEdit);

        var share = await (await GetPageAsync(browser, $"/polls/{points}/share")).Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"About How many\"", share);
        Assert.Contains("aria-label=\"About Aliases\"", share);
    }
}
