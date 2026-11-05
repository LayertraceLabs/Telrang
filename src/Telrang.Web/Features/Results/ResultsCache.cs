using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Telrang.Web.Features.Results;

/// <summary>
/// Computed results per poll in memory (DESIGN.md §31, pentest R3-RES-1; PERFORMANCE.md "Known bottlenecks"). Ballots stay
/// raw and results are still computed from them; this only keeps one computation until something changes:
/// <list type="bullet">
/// <item>an entry is dropped on <see cref="PollChangeNotifier.Changed"/> / <see cref="PollChangeNotifier.End"/> (every
/// committed write that changes what the results page shows), and checked against the stored definition on read;</item>
/// <item>a change that lands while an entry is being computed bumps a version stripe, so that stale computation is never
/// stored;</item>
/// <item>one computation per poll at a time: parallel requests for the same poll wait for it;</item>
/// <item>the total size is bounded (<c>Results:CacheMegabytes</c>, default 64, estimated from ballots × options), entries
/// expire after 10 minutes unused, and the cache empties itself when the process nears its memory limit.</item>
/// </list>
/// Memory only, one instance per process.
/// </summary>
public sealed class ResultsCache : IDisposable
{
    private const int Stripes = 4096;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(30);

    private readonly MemoryCache cache;
    private readonly long[] versions = new long[Stripes];
    private readonly ConcurrentDictionary<Guid, Flight> computing = new();
    private readonly PollChangeNotifier notifier;

    public ResultsCache(PollChangeNotifier notifier, IConfiguration config)
    {
        this.notifier = notifier;
        LimitBytes = Math.Max(1, config.GetValue("Results:CacheMegabytes", 64)) * 1024L * 1024L;
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = LimitBytes });
        notifier.Invalidated += Drop;
    }

    /// <summary>Most bytes (estimated) the entries may hold together.</summary>
    public long LimitBytes { get; }

    /// <summary>Entries held now (tests).</summary>
    public int Count => cache.Count;

    /// <summary>Computations under way (tests).</summary>
    public int Computing => computing.Count;

    /// <summary>
    /// The poll's results data computed from <paramref name="definitionJson"/> (the stored definition the caller's access
    /// check read): from the cache when an entry for this definition exists, else by <paramref name="compute"/>, shared
    /// with parallel callers and stored unless the poll changed meanwhile.
    /// </summary>
    public async Task<PollResultsData> GetAsync(Guid pollId, string definitionJson, Func<CancellationToken, Task<PollResultsData>> compute,
        CancellationToken ct)
    {
        if (cache.TryGetValue(pollId, out PollResultsData? hit) && hit is not null && hit.DefinitionJson == definitionJson) return hit;
        var version = Volatile.Read(ref versions[Stripe(pollId)]);
        // Shared only among callers that saw the same version: a caller after a change never gets an older computation.
        var flight = computing.AddOrUpdate(pollId, _ => new Flight(version, this, pollId, compute),
            (_, running) => running.Version == version ? running : new Flight(version, this, pollId, compute));
        var data = await flight.Task.Value.WaitAsync(ct);
        // A computation shared with a caller that read another definition (an edit landed between): compute our own.
        return data.DefinitionJson == definitionJson ? data : await compute(ct);
    }

    private sealed class Flight
    {
        public Flight(long version, ResultsCache owner, Guid pollId, Func<CancellationToken, Task<PollResultsData>> compute)
        {
            Version = version;
            Task = new(() => owner.ComputeAsync(pollId, compute, version, this));
        }

        public long Version { get; }
        public Lazy<Task<PollResultsData>> Task { get; }
    }

    private async Task<PollResultsData> ComputeAsync(Guid pollId, Func<CancellationToken, Task<PollResultsData>> compute, long version,
        Flight flight)
    {
        try
        {
            // Not the caller's token: other callers share this computation.
            var data = await compute(CancellationToken.None);
            if (Volatile.Read(ref versions[Stripe(pollId)]) == version)
            {
                if (NearMemoryLimit()) cache.Compact(1.0);
                cache.Set(pollId, data, new MemoryCacheEntryOptions
                {
                    Size = Math.Min(data.EstimatedBytes, LimitBytes),
                    SlidingExpiration = Idle,
                    AbsoluteExpirationRelativeToNow = Longest,
                });
            }
            return data;
        }
        finally
        {
            // The computation removes itself, whether or not anyone still waits: a finished flight kept here would hold the
            // full data outside the size limit, and a failed one would hand its exception to the next caller.
            computing.TryRemove(new(pollId, flight));
        }
    }

    private void Drop(Guid pollId)
    {
        Interlocked.Increment(ref versions[Stripe(pollId)]);
        cache.Remove(pollId);
        computing.TryRemove(pollId, out _); // a running one finishes for its callers, but nobody new joins it
    }

    private static int Stripe(Guid pollId) => (pollId.GetHashCode() & int.MaxValue) % Stripes;

    /// <summary>The GC reports memory use near its high-load threshold (a container limit or <c>GCHeapHardLimit</c>).</summary>
    private static bool NearMemoryLimit()
    {
        var info = GC.GetGCMemoryInfo();
        return info.HighMemoryLoadThresholdBytes > 0 && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes * 9 / 10;
    }

    public void Dispose()
    {
        notifier.Invalidated -= Drop;
        cache.Dispose();
    }
}
