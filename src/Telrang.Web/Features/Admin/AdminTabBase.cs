using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Telrang.Web.Features.Admin;

/// <summary>
/// Shared plumbing for the admin tabs: runs each <see cref="AdminService"/> call in a fresh DI scope
/// (UserManager/DbContext must not live as long as the circuit) and keeps the status line.
/// </summary>
public abstract class AdminTabBase : ComponentBase
{
    [CascadingParameter] protected Task<AuthenticationState> AuthState { get; set; } = default!;
    [Inject] protected IServiceScopeFactory ScopeFactory { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected string? StatusMessage;
    protected bool StatusOk = true;
    protected string? StatusLink;
    protected bool Busy;

    protected async Task<ClaimsPrincipal> CallerAsync() => (await AuthState).User;

    protected async Task<T> WithAdminAsync<T>(Func<AdminService, ClaimsPrincipal, Task<T>> action)
    {
        var caller = await CallerAsync();
        await using var scope = ScopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AdminService>(), caller);
    }

    /// <summary>Runs an action, shows its outcome, then reloads the tab.</summary>
    protected async Task RunAsync(Func<AdminService, ClaimsPrincipal, Task<AdminOutcome>> action)
    {
        Busy = true;
        try
        {
            var outcome = await WithAdminAsync(action);
            StatusMessage = outcome.Message;
            StatusOk = outcome.Ok;
            StatusLink = outcome.Link;
            await OnActionDoneAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    protected virtual Task OnActionDoneAsync() => Task.CompletedTask;
}
