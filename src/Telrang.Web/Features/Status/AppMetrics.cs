using System.Diagnostics.Metrics;

namespace Telrang.Web.Features.Status;

/// <summary>
/// The app's counters (DESIGN.md §14.8), created through <see cref="IMeterFactory"/> so they belong
/// to this host. <see cref="RateCounters"/> listens to them in-process for the admin Status tab; any
/// OpenTelemetry exporter could read the same meter.
/// </summary>
public sealed class AppMetrics
{
    public const string MeterName = "Telrang";

    public AppMetrics(IMeterFactory factory)
    {
        Meter = factory.Create(MeterName);
        Requests = Meter.CreateCounter<long>("av.requests", description: "HTTP requests");
        TooManyRequests = Meter.CreateCounter<long>("av.too_many_requests", description: "429 responses");
        FailedLogins = Meter.CreateCounter<long>("av.failed_logins", description: "Failed sign-ins (password, 2FA, recovery code)");
        BallotsSubmitted = Meter.CreateCounter<long>("av.ballots_submitted", description: "Ballots saved (new or revised)");
        MailsSent = Meter.CreateCounter<long>("av.mails_sent", description: "Mails delivered");
        MailsFailed = Meter.CreateCounter<long>("av.mails_failed", description: "Mail delivery attempts that failed");
    }

    public Meter Meter { get; }
    public Counter<long> Requests { get; }
    public Counter<long> TooManyRequests { get; }
    public Counter<long> FailedLogins { get; }
    public Counter<long> BallotsSubmitted { get; }
    public Counter<long> MailsSent { get; }
    public Counter<long> MailsFailed { get; }

    /// <summary>(instrument name, label) in display order.</summary>
    public static readonly IReadOnlyList<(string Name, string Label)> Rows =
    [
        ("av.requests", "Requests"),
        ("av.too_many_requests", "429 responses"),
        ("av.failed_logins", "Failed sign-ins"),
        ("av.ballots_submitted", "Ballots submitted"),
        ("av.mails_sent", "Mails sent"),
        ("av.mails_failed", "Mail failures"),
    ];
}

/// <summary>
/// In-process <see cref="MeterListener"/> over <see cref="AppMetrics"/>: per-minute ring of the last
/// 24 h per instrument, so the Status tab can show 1 h and 24 h totals. Memory only.
/// </summary>
public sealed class RateCounters : IDisposable
{
    private const int Minutes = 24 * 60;

    private sealed class Ring
    {
        public readonly long[] Counts = new long[Minutes];
        public readonly long[] Stamps = new long[Minutes];
    }

    private readonly Dictionary<string, Ring> rings = new(StringComparer.Ordinal);
    private readonly MeterListener listener = new();
    private readonly TimeProvider time;

    public RateCounters(AppMetrics metrics, TimeProvider time)
    {
        this.time = time;
        foreach (var (name, _) in AppMetrics.Rows) rings[name] = new Ring();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter) && rings.ContainsKey(instrument.Name)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Add(instrument.Name, value));
        listener.Start();
    }

    private long Minute => time.GetUtcNow().ToUnixTimeSeconds() / 60;

    private void Add(string name, long value)
    {
        if (!rings.TryGetValue(name, out var ring)) return;
        var minute = Minute;
        var i = (int)(minute % Minutes);
        lock (ring)
        {
            if (ring.Stamps[i] != minute)
            {
                ring.Stamps[i] = minute;
                ring.Counts[i] = 0;
            }
            ring.Counts[i] += value;
        }
    }

    /// <summary>Sum over the last <paramref name="span"/> (whole minutes, at most 24 h).</summary>
    public long Sum(string name, TimeSpan span)
    {
        if (!rings.TryGetValue(name, out var ring)) return 0;
        var now = Minute;
        var oldest = now - Math.Min(Minutes, (long)Math.Ceiling(span.TotalMinutes)) + 1;
        long total = 0;
        lock (ring)
        {
            for (var i = 0; i < Minutes; i++)
            {
                if (ring.Stamps[i] >= oldest && ring.Stamps[i] <= now) total += ring.Counts[i];
            }
        }
        return total;
    }

    public void Dispose() => listener.Dispose();
}

public sealed record LogRingEntry(DateTimeOffset At, LogLevel Level, string Category, string Message);

/// <summary>Last 200 log entries at Warning or above, in memory only (DESIGN.md §14.8). Lines carrying a secret
/// (<see cref="Telrang.Web.Security.SecretLogEvents"/>) are never added.</summary>
public sealed class LogRing(TimeProvider time)
{
    public const int Capacity = 200;
    private readonly LinkedList<LogRingEntry> entries = new();

    public void Add(LogLevel level, string category, string message)
    {
        var entry = new LogRingEntry(time.GetUtcNow(), level, category, message.Length > 2000 ? message[..2000] : message);
        lock (entries)
        {
            entries.AddFirst(entry);
            while (entries.Count > Capacity) entries.RemoveLast();
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<LogRingEntry> Snapshot()
    {
        lock (entries) return entries.ToList();
    }

    public void Clear()
    {
        lock (entries) entries.Clear();
    }
}

/// <summary>Feeds <see cref="LogRing"/>. Registered as an <see cref="ILoggerProvider"/> in DI.</summary>
public sealed class LogRingProvider(LogRing ring) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RingLogger(ring, categoryName);

    public void Dispose() { }

    private sealed class RingLogger(LogRing ring, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || Telrang.Web.Security.SecretLogEvents.IsSecret(eventId)) return; // keys: console only
            var message = formatter(state, exception);
            if (exception is not null) message += $" ({exception.GetType().Name}: {exception.Message})";
            ring.Add(logLevel, category, message);
        }
    }
}

/// <summary>Live Blazor circuits (Status tab).</summary>
public sealed class CircuitCounter
{
    private int count;
    public int Count => Volatile.Read(ref count);
    public void Opened() => Interlocked.Increment(ref count);
    public void Closed() => Interlocked.Decrement(ref count);
}

/// <summary>Process start time and the data directory (Status tab).</summary>
public sealed record AppStartInfo(DateTimeOffset StartedAt, string DataDir);
