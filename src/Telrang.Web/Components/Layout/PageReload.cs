using Microsoft.JSInterop;

namespace Telrang.Web.Components.Layout;

/// <summary>
/// A full reload from an interactive component (DESIGN.md §24.6). <c>NavigateTo(uri, forceLoad: true)</c> unloads the
/// page while the circuit's call is still in flight (a failed navigation in the server log, "WebSocket is not in the
/// OPEN state" in the browser); <c>js/reload.js</c> answers first and reloads on the next task.
/// </summary>
public static class PageReload
{
    public static async Task SoonAsync(IJSRuntime js)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/reload.js");
            await module.InvokeVoidAsync("reloadSoon");
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { } // the module did not load: the next page load shows the new layout
        catch (TaskCanceledException) { }
    }
}
