using System.Text.RegularExpressions;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §14.2: pages show facts as labels, badges and headers, explanations go in <c>OptionHelp</c>, a
/// <c>&lt;details&gt;</c> disclosure or a <c>title</c>. A source scan of the visible paragraphs of every page.
/// </summary>
public partial class CopyTests
{
    /// <summary>Longest visible paragraph or form hint, in words (Razor expressions count as one).</summary>
    private const int MaxWords = 12;

    private static string WebSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Telrang.Web");
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"RenderFragment \w*Help\(\)")]
    private static partial Regex HelpFragment();

    [GeneratedRegex(@"<(OptionHelp|details)\b")]
    private static partial Regex HelpOpen();

    [GeneratedRegex(@"</(OptionHelp|details)>")]
    private static partial Regex HelpClose();

    [GeneratedRegex(@"<p[ >]|form-text")]
    private static partial Regex Paragraph();

    /// <summary>Alerts and status messages say what happened or what to change; they are not explanations.</summary>
    [GeneratedRegex("""role="(alert|status)"|class="[^"]*alert""")]
    private static partial Regex Alert();

    [GeneratedRegex(@"@\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)|@[\w.]+(\([^)]*\))?")]
    private static partial Regex RazorExpression();

    /// <summary>A Razor control line inside a paragraph (<c>@if (…)</c>, <c>else</c>, braces, a <c>var</c>).</summary>
    [GeneratedRegex(@"^\s*(@?(if|else|foreach|for|switch|var)\b.*|[{}]|)\s*$")]
    private static partial Regex ControlLine();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[A-Za-z][\w'-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(You can|Simply|Please|Feel free|Note that)\b")]
    private static partial Regex Filler();

    [Fact]
    public void Pages_carry_no_paragraphs_of_explanation()
    {
        var problems = new List<string>();
        var files = Directory.EnumerateFiles(WebSource(), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 50, $"only {files.Count} pages found");
        foreach (var file in files)
        {
            var lines = RazorComment().Replace(File.ReadAllText(file), "").Split('\n');
            var depth = 0;
            var inHelpFragment = false;
            for (var n = 0; n < lines.Length; n++)
            {
                var line = lines[n];
                var where = $"{Path.GetRelativePath(WebSource(), file)}:{n + 1}";
                var text = Tag().Replace(RazorExpression().Replace(line, "X"), "");
                if (Filler().IsMatch(text)) problems.Add($"{where}: filler phrase: {text.Trim()}");

                // An editor help fragment (TypeHelp() etc.) is rendered inside an OptionHelp elsewhere.
                if (HelpFragment().IsMatch(line)) inHelpFragment = true;
                if (inHelpFragment)
                {
                    if (line.Trim() == "};") inHelpFragment = false;
                    continue;
                }
                var opens = HelpOpen().Matches(line).Count;
                if (depth == 0 && opens == 0 && Paragraph().IsMatch(line) && !Alert().IsMatch(line))
                {
                    // A paragraph over several lines counts as a whole.
                    var whole = line;
                    for (var k = n + 1; whole.Contains("<p", StringComparison.Ordinal) && !whole.Contains("</p>", StringComparison.Ordinal) && k < lines.Length; k++)
                        if (!ControlLine().IsMatch(lines[k])) whole += " " + lines[k];
                    if (whole.Contains("<p", StringComparison.Ordinal)) text = Tag().Replace(RazorExpression().Replace(whole, "X"), "");
                    var words = Word().Matches(text).Count;
                    if (words > MaxWords) problems.Add($"{where}: {words} words on the page; use a label, badge, title or OptionHelp: {text.Trim()}");
                }
                depth += opens - HelpClose().Matches(line).Count;
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
