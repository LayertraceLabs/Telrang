using System.Text.RegularExpressions;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §23.1: a failing JS module never kills a circuit; copy buttons run in the browser.</summary>
public partial class JsResilienceTests
{
    private static string WebSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Telrang.Web");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(WebSource(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>JS interop calls: a module or runtime call by identifier (first argument a string literal), focusing an
    /// element, and disposing a module reference.</summary>
    [GeneratedRegex("""\.Invoke(?:Void)?Async(?:<[^>()]*>)?\(\s*"|\.FocusAsync\(|\b\w*[Mm]odule\??\.DisposeAsync\(""")]
    private static partial Regex JsCall();

    [GeneratedRegex("""\Gcatch\s*\(\s*([\w.]+)""")]
    private static partial Regex CatchClause();

    /// <summary>
    /// Every JS interop call in the web project sits in a <c>try</c> with a <c>catch (JSException)</c> (and so cannot
    /// end the circuit when a module fails to load or throws). A source scan: it walks out from each call through the
    /// enclosing blocks until it finds a <c>try</c> whose catch clauses include <c>JSException</c>.
    /// </summary>
    [Fact]
    public void Every_js_interop_call_catches_JSException()
    {
        var problems = new List<string>();
        var calls = 0;
        foreach (var file in SourceFiles())
        {
            var source = File.ReadAllText(file);
            foreach (Match call in JsCall().Matches(source))
            {
                calls++;
                if (!CaughtByJsException(source, call.Index))
                {
                    var line = source[..call.Index].Count(c => c == '\n') + 1;
                    problems.Add($"{Path.GetRelativePath(WebSource(), file)}:{line}: JS call without catch (JSException)");
                }
            }
        }
        Assert.True(calls >= 10, $"only {calls} JS calls found: the scan no longer matches the code");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static bool CaughtByJsException(string source, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var c = source[i];
            if (c == '}') depth++;
            else if (c == '{')
            {
                if (depth > 0) { depth--; continue; }
                // An enclosing block opens here: is it a try with a JSException catch?
                if (source[..i].TrimEnd().EndsWith("try", StringComparison.Ordinal) && CatchesJsException(source, i)) return true;
            }
        }
        return false;
    }

    private static bool CatchesJsException(string source, int open)
    {
        var end = MatchingBrace(source, open);
        while (end > 0)
        {
            var next = end + 1;
            while (next < source.Length && char.IsWhiteSpace(source[next])) next++;
            var m = CatchClause().Match(source, next);
            if (!m.Success) return false;
            if (m.Groups[1].Value is "JSException" or "Microsoft.JSInterop.JSException") return true;
            var block = source.IndexOf('{', m.Index);
            end = block < 0 ? -1 : MatchingBrace(source, block);
        }
        return false;
    }

    private static int MatchingBrace(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    [Fact]
    public async Task Copy_buttons_carry_their_text_and_status_target()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        var html = await (await browser.GetAsync($"/polls/{id}/share")).Content.ReadAsStringAsync();
        Assert.Contains($"data-copy=\"https://localhost/p/{id}\" data-copy-status=\"public-link-status\"", html);
        Assert.Contains("id=\"public-link-status\" role=\"status\"", html);
        Assert.Matches("""<script src="[^"]*js/clipboard(\.[\w]+)?\.js" type="module"></script>""", html);

        // No copy goes through the server any more: clipboard.js exports nothing a component could call.
        var module = File.ReadAllText(Path.Combine(WebSource(), "wwwroot", "js", "clipboard.js"));
        Assert.DoesNotContain("export ", module);
        Assert.Contains("[data-copy]", module);
    }
}
