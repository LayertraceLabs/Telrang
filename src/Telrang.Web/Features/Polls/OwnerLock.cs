namespace Telrang.Web.Features.Polls;

/// <summary>
/// One async lock per account for count-then-insert limits (open polls, polls per day, invites per poll): parallel
/// requests of one owner are serialised, so they cannot all pass the count before any of them writes (pentest
/// R2-POLLS-5). In process, which is enough for the single-instance deployment (SQLite). Entries are removed when the
/// last holder or waiter leaves.
/// </summary>
public static class OwnerLock
{
    private static readonly Dictionary<string, Entry> Gates = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    public static async Task<IDisposable> AcquireAsync(string ownerId, CancellationToken ct = default)
    {
        Entry entry;
        lock (Gates)
        {
            if (!Gates.TryGetValue(ownerId, out entry!)) Gates[ownerId] = entry = new Entry();
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Leave(ownerId, entry);
            throw;
        }
        return new Releaser(ownerId, entry);
    }

    private static void Leave(string ownerId, Entry entry)
    {
        lock (Gates)
        {
            if (--entry.Users == 0) Gates.Remove(ownerId);
        }
    }

    private sealed class Releaser(string ownerId, Entry entry) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            entry.Gate.Release();
            Leave(ownerId, entry);
        }
    }
}
