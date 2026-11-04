using Telrang.Web.Features.Status;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Telrang.Web.Security;

/// <summary>
/// The abuse-tracker key of the current client (IPv4 /32, IPv6 /48), for services that run both in
/// HTTP requests and in interactive circuits. In a circuit the request is gone, so
/// <see cref="AppCircuitHandler"/> captures the key when the circuit connects. Memory only.
/// </summary>
public sealed class ClientInfo(IHttpContextAccessor accessor)
{
    private string? captured;
    private System.Net.IPAddress? capturedAddress;

    public string? Key => captured ?? (accessor.HttpContext is { } http ? ClientKeys.ForAbuse(http) : null);

    /// <summary>
    /// The client's address, only for the blocked-range check against the acting admin's own address (pentest R2-ACC-7).
    /// Memory only; never logged or stored.
    /// </summary>
    public System.Net.IPAddress? Address => capturedAddress ?? accessor.HttpContext?.Connection.RemoteIpAddress;

    public void Capture()
    {
        if (accessor.HttpContext is not { } http) return;
        captured = ClientKeys.ForAbuse(http);
        capturedAddress = http.Connection.RemoteIpAddress;
    }
}

/// <summary>
/// Counts live circuits, captures the client key for <see cref="ClientInfo"/>, and refuses a circuit beyond the
/// <see cref="CircuitLimiter"/> caps: per account when signed in, per client key always, "unknown" included (pentest AV-09,
/// R2-POLLS-6, R2-VOTE-3d; no abuse signal, R2-VOTE-3c). Throwing from <see cref="OnCircuitOpenedAsync"/> fails the
/// circuit's start, so it never runs. An <see cref="OutOfMemoryException"/> from any inbound activity of the circuit (an
/// event, a JS call) records <see cref="AbuseSignal.CircuitOutOfMemory"/> for its client key and account (DESIGN.md §31,
/// pentest R3-RES-7b) and is passed on unchanged.
/// </summary>
public sealed class AppCircuitHandler(CircuitCounter counter, ClientInfo client, CircuitLimiter limiter, IHttpContextAccessor accessor,
    AbuseTracker abuse) : CircuitHandler
{
    private IDisposable? lease;
    private bool counted;
    private string? accountId;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        client.Capture();
        // The circuit's connection request carries the sign-in cookie, so its user is the circuit's account.
        var userId = accessor.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        accountId = userId;
        // No abuse signal on refusal (R2-VOTE-3c): tabs behind one NAT are no attack; the caps alone bound the cost.
        lease = limiter.TryAcquire(client.Key ?? "unknown", userId)
                ?? throw new InvalidOperationException("Too many open circuits from one client or account.");
        counter.Opened();
        counted = true;
        return Task.CompletedTask;
    }

    /// <summary>The circuit activity running in this async flow, for <see cref="OnFirstChance"/>.</summary>
    private static readonly AsyncLocal<Activity?> Current = new();

    private sealed class Activity(AppCircuitHandler handler)
    {
        public AppCircuitHandler Handler { get; } = handler;
        public int Recorded;
    }

    static AppCircuitHandler() => AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

    /// <summary>
    /// The renderer catches an event handler's exception itself (it ends the circuit without rethrowing), so the activity
    /// handler alone would miss it; the first-chance notification still sees it, in the activity's async flow.
    /// </summary>
    private static void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is OutOfMemoryException && Current.Value is { } activity) activity.Handler.RecordOutOfMemory(activity);
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var activity = new Activity(this);
            Current.Value = activity;
            try
            {
                await next(context);
            }
            catch (OutOfMemoryException)
            {
                RecordOutOfMemory(activity);
                throw;
            }
            finally
            {
                Current.Value = null;
            }
        };

    /// <summary>Records the signal once per activity; nothing more can be done when even that runs out of memory.</summary>
    private void RecordOutOfMemory(Activity activity)
    {
        if (Interlocked.Exchange(ref activity.Recorded, 1) == 1) return;
        try { abuse.Record(client.Key, accountId, AbuseSignal.CircuitOutOfMemory); }
        catch (OutOfMemoryException) { }
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        client.Capture();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (counted) counter.Closed();
        counted = false;
        lease?.Dispose();
        lease = null;
        return Task.CompletedTask;
    }
}
