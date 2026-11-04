using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Telrang.Web.Data;

/// <summary>
/// Write transactions for both providers (DESIGN.md §28). The app's multi-step writes were written for SQLite, whose
/// <c>BEGIN IMMEDIATE</c> (Microsoft.Data.Sqlite's default) lets one write transaction run at a time. PostgreSQL runs
/// them side by side, so each one takes a row lock on the one row of <see cref="WriterLock"/> that gives the same
/// guarantee:
/// <list type="bullet">
/// <item><see cref="BeginWriteAsync"/>: the writer row <c>FOR UPDATE</c>, so the transaction runs alone among write
/// transactions, as on SQLite. Every explicit transaction uses it unless it is a ballot write.</item>
/// <item><see cref="BeginPollWriteAsync"/>: the writer row <c>FOR SHARE</c> plus a row lock on the poll (<c>SELECT … FOR
/// UPDATE</c>): ballot writes on different polls run in parallel, writes on one poll one after another, and none runs
/// beside an exclusive writer. For the hot path (votes).</item>
/// </list>
/// Both locks end with the transaction (commit, rollback or dispose). On SQLite these are plain transactions.
/// A row lock needs the UPDATE privilege on the table (pentest R3-DEP-1): only Telrang's own role can take it, not a
/// read-only reporting role or another role on the same server, which could take a global advisory lock.
/// Code never calls <c>Database.BeginTransactionAsync</c> directly (<c>ConcurrencyTests</c> checks the sources).
/// </summary>
public static class DbWrites
{
    /// <summary>The id of the one row of <c>WriterLocks</c> (created at startup, <see cref="EnsureWriterLockAsync"/>).</summary>
    public const int WriterLockId = 1;

    /// <summary>A write transaction that runs alone among write transactions (on PostgreSQL: the writer row FOR UPDATE).</summary>
    public static async Task<IDbContextTransaction> BeginWriteAsync(this DatabaseFacade db, CancellationToken ct = default)
    {
        var tx = await db.BeginTransactionAsync(ct);
        try
        {
            if (db.IsNpgsql())
                await LockWriterRowAsync(db.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"WriterLocks\" WHERE \"Id\" = {WriterLockId} FOR UPDATE"), ct);
            return tx;
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    /// <summary>A write transaction on one poll's ballots (on PostgreSQL: the writer row FOR SHARE and the poll's row lock).</summary>
    public static async Task<IDbContextTransaction> BeginPollWriteAsync(this DatabaseFacade db, Guid pollId, CancellationToken ct = default)
    {
        var tx = await db.BeginTransactionAsync(ct);
        try
        {
            if (db.IsNpgsql())
            {
                await LockWriterRowAsync(db.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"WriterLocks\" WHERE \"Id\" = {WriterLockId} FOR SHARE"), ct);
                await db.ExecuteSqlAsync($"SELECT 1 FROM \"Polls\" WHERE \"Id\" = {pollId} FOR UPDATE", ct);
            }
            return tx;
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs the lock query; a missing row would lock nothing, so it is an error rather than a silent pass.</summary>
    private static async Task LockWriterRowAsync(IQueryable<int> query, CancellationToken ct)
    {
        if ((await query.ToListAsync(ct)).Count != 1)
            throw new InvalidOperationException("The writer lock row is missing (table WriterLocks). Restart the app to create it.");
    }

    /// <summary>
    /// Creates the writer lock row on PostgreSQL when it is missing (startup, after the migrations). Not part of the
    /// migrations, so <c>copy-to-postgres</c> still finds a freshly migrated database empty.
    /// </summary>
    public static async Task EnsureWriterLockAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsNpgsql()) return;
        await db.Database.ExecuteSqlAsync($"INSERT INTO \"WriterLocks\" (\"Id\") VALUES ({WriterLockId}) ON CONFLICT DO NOTHING", ct);
    }
}

/// <summary>
/// The row whose lock stands in for SQLite's single writer on PostgreSQL (<see cref="DbWrites"/>). One row, id
/// <see cref="DbWrites.WriterLockId"/>; unused on SQLite.
/// </summary>
public class WriterLock
{
    public int Id { get; set; }
}
