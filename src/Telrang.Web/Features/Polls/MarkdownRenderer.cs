using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// Renders user Markdown (poll and option descriptions, the home page text, the legal page). Raw HTML is disabled
/// (escaped, never passed through); links get rel="noopener noreferrer" target="_blank"; link URLs with a scheme other
/// than http/https/mailto are dropped. Images render as their alt text, never as <c>&lt;img&gt;</c> (DESIGN.md §31,
/// pentest R3-RES-4: a page of same-origin images turned every visitor into a request flood). Text that Markdig refuses
/// (its nesting limit throws) is shown as encoded plain text (pentest R2-POLLS-4).
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>
    /// Longest text parsed as Markdown: the ceiling of the poll description limit. Markdig's cost grows faster than
    /// linearly on some inputs (unclosed links), so longer text is shown as plain text (pentest R2-POLLS-1).
    /// </summary>
    public const int MaxLength = 10_000;

    /// <summary>Cap for the admin's legal page, which has no length limit of its own.</summary>
    public const int AdminMaxLength = 100_000;

    public static MarkupString Render(string? markdown, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new MarkupString("");
        if (markdown.Length > maxLength) return PlainText(markdown);
        try
        {
            return new MarkupString(ToHtml(markdown));
        }
        catch (Exception)
        {
            // Markdig throws on nesting deeper than its limit (">" x 128, long emphasis runs); any parser failure ends here.
            return PlainText(markdown);
        }
    }

    /// <summary>The text HTML-encoded in one paragraph, line breaks kept.</summary>
    internal static MarkupString PlainText(string text) =>
        new("<p>" + System.Net.WebUtility.HtmlEncode(text.Trim()).ReplaceLineEndings("<br>\n") + "</p>");

    private static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        // Images first: each becomes a literal of its alt text, which the renderer HTML-encodes. A nested image goes with
        // its outer one (its text is part of the outer alt text).
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
            if (image.Parent is not null) image.ReplaceBy(new LiteralInline(AltText(image)));
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "#";
            if (!link.IsImage)
            {
                var attrs = link.GetAttributes();
                attrs.AddPropertyIfNotExist("rel", "noopener noreferrer");
                attrs.AddPropertyIfNotExist("target", "_blank");
            }
        }
        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "#";
            var attrs = link.GetAttributes();
            attrs.AddPropertyIfNotExist("rel", "noopener noreferrer");
            attrs.AddPropertyIfNotExist("target", "_blank");
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>The plain text of an image's alt inlines (nested emphasis, code and images included).</summary>
    private static string AltText(ContainerInline image)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in image.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal: sb.Append(literal.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
            }
        }
        return sb.ToString();
    }

    internal static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var trimmed = url.Trim();
        // Relative URLs (no scheme) are fine; a ':' before any '/', '?' or '#' means a scheme.
        var colon = trimmed.IndexOf(':');
        if (colon < 0) return true;
        var firstDelimiter = trimmed.IndexOfAny(['/', '?', '#']);
        if (firstDelimiter >= 0 && firstDelimiter < colon) return true;
        var scheme = trimmed[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }
}
