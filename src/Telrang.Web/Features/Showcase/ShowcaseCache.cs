using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Telrang.Web.Features.Showcase;

/// <summary>
/// The home page's examples rendered once (DESIGN.md §31, pentest R3-RES-6): the markup depends only on the build and the
/// day (the dates example starts today, UTC), never on the visitor, the settings or the theme (colours come from the
/// stylesheet), so it is rendered once per day and process and reused for every view. Memory only.
/// </summary>
public sealed class ShowcaseCache(IServiceProvider services, ILoggerFactory logs, TimeProvider clock)
{
    /// <summary>The sprite reference the renderer outside a request writes (no asset map: the bare path).</summary>
    private const string BareSprite = "\"icons.svg#";

    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateOnly Day, string Sprite, MarkupString Html)? cached;

    /// <summary>
    /// The rendered <see cref="Showcase"/> for today. <paramref name="spriteUrl"/> is the page's own <c>Assets["icons.svg"]</c>
    /// (the fingerprinted address), which replaces the bare path the renderer outside a request writes.
    /// </summary>
    public async Task<MarkupString> GetAsync(string spriteUrl)
    {
        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (cached is { } c && c.Day == day && c.Sprite == spriteUrl) return c.Html;
        await gate.WaitAsync();
        try
        {
            if (cached is { } again && again.Day == day && again.Sprite == spriteUrl) return again.Html;
            await using var scope = services.CreateAsyncScope();
            await using var renderer = new HtmlRenderer(scope.ServiceProvider, logs);
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Showcase>()).ToHtmlString());
            var markup = new MarkupString(html.Replace(BareSprite, "\"" + spriteUrl + "#", StringComparison.Ordinal));
            cached = (day, spriteUrl, markup);
            return markup;
        }
        finally
        {
            gate.Release();
        }
    }
}
