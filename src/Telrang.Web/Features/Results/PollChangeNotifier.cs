using System.Threading.Channels;

namespace Telrang.Web.Features.Results;

/// <summary>
/// Tells the owner's live results streams (DESIGN.md §21.2) that a poll changed. Memory only, one instance per process
/// (single container). Writers call <see cref="Changed"/> after a committed write that changes what the results page
/// shows, and <see cref="End"/> when the poll is gone or no longer the streaming owner's (delete, transfer, retention).
/// Each subscriber has a bounded channel of capacity 1 that drops further writes, so a burst becomes one event.
/// </summary>
public sealed class PollChangeNotifier
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, HashSet<Subscription>> subscribers = [];

    /// <summary>
    /// Raised by <see cref="Changed"/> and <see cref="End"/> before the streams hear of it: the results cache drops the
    /// poll's entry (DESIGN.md §31).
    /// </summary>
    public event Action<Guid>? Invalidated;

    /// <summary>A change notice for every open stream of the poll (coalesced per stream).</summary>
    public void Changed(Guid pollId)
    {
        Invalidated?.Invoke(pollId);
        Subscription[] subs;
        lock (gate)
        {
            if (!subscribers.TryGetValue(pollId, out var set)) return;
            subs = [.. set];
        }
        foreach (var sub in subs) sub.Signal();
    }

    /// <summary>Ends every open stream of the poll at once.</summary>
    public void End(Guid pollId)
    {
        Invalidated?.Invoke(pollId);
        HashSet<Subscription>? set;
        lock (gate)
        {
            if (!subscribers.Remove(pollId, out set)) return;
        }
        foreach (var sub in set) sub.Complete();
    }

    /// <summary>Open streams of one poll (tests).</summary>
    public int Count(Guid pollId)
    {
        lock (gate) return subscribers.TryGetValue(pollId, out var set) ? set.Count : 0;
    }

    /// <summary>Starts listening; dispose the subscription to stop.</summary>
    public Subscription Subscribe(Guid pollId)
    {
        var sub = new Subscription(this, pollId);
        lock (gate)
        {
            if (!subscribers.TryGetValue(pollId, out var set)) subscribers[pollId] = set = [];
            set.Add(sub);
        }
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (gate)
        {
            if (!subscribers.TryGetValue(sub.PollId, out var set) || !set.Remove(sub)) return;
            if (set.Count == 0) subscribers.Remove(sub.PollId);
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly PollChangeNotifier owner;
        private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        private int ended;

        internal Subscription(PollChangeNotifier owner, Guid pollId)
        {
            this.owner = owner;
            PollId = pollId;
        }

        public Guid PollId { get; }

        /// <summary>Completes when a change is pending; false once the poll's streams were ended.</summary>
        public ChannelReader<bool> Reader => channel.Reader;

        /// <summary><see cref="PollChangeNotifier.End"/> was called for the poll.</summary>
        public bool Ended => Volatile.Read(ref ended) == 1;

        internal void Signal() => channel.Writer.TryWrite(true);

        internal void Complete()
        {
            Volatile.Write(ref ended, 1);
            channel.Writer.TryComplete();
        }

        public void Dispose()
        {
            channel.Writer.TryComplete();
            owner.Remove(this);
        }
    }
}
