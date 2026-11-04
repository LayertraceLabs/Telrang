using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Telrang.Web.Components.Layout;

/// <summary>
/// Base of the app shells (DESIGN.md §11): <see cref="NavOpen"/> backs the CSS-only phone menu toggle and is
/// reset after each in-app navigation (on static pages navigation is a full load, which resets it too).
/// </summary>
public abstract class ShellBase : LayoutComponentBase, IDisposable
{
    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    protected bool NavOpen;

    protected override void OnInitialized() => Navigation.LocationChanged += OnLocationChanged;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (!NavOpen) return;
        NavOpen = false;
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Navigation.LocationChanged -= OnLocationChanged;
        GC.SuppressFinalize(this);
    }
}
