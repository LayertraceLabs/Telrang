using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Telrang.Web.Data;

/// <summary>
/// <c>copy-to-postgres --from &lt;sqlite file&gt;</c> (DESIGN.md §28): copies a stopped SQLite instance into an empty
/// PostgreSQL database named by the environment (<c>Database__Provider=Postgres</c> and the connection string). Applies the
/// PostgreSQL migrations, refuses a target with any Telrang rows and a SQLite file whose migrations are not exactly this
/// version's, copies every table in one transaction (ids, Identity rows, settings, outbox and change logs as they are),
/// resets the identity sequences and compares the row count of every table before committing. Prints table names and
/// counts only, never data or the connection string. Refuses a SQLite file another process has open (pentest R3-DEP-12b)
/// and a target holding another application's tables or migrations, before migrating (R3-DEP-12a). Reads the SQLite file
/// read-only (the in-use check takes its lock for a moment and writes nothing): it is never changed, deleted or
/// renamed. Exit code 0 only on a verified copy.
/// </summary>
public static class CopyToPostgresCommand
{
    public const string Name = "copy-to-postgres";

    public const int Ok = 0;
    /// <summary>The copy started and failed, or the counts differ; nothing was committed.</summary>
    public const int Failed = 1;
    /// <summary>Wrong arguments.</summary>
    public const int Usage = 2;
    /// <summary>A precondition does not hold (environment, SQLite file, non-empty target); nothing was written.</summary>
    public const int Refused = 3;

    private const int BatchSize = 500;
    private const string UsageText = "Usage: copy-to-postgres --from <sqlite file>";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, DatabaseSetup target, string dataDir, TextWriter output,
        TextWriter error, CancellationToken ct = default)
    {
        string? from = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--from" && i + 1 < args.Count && from is null) from = args[++i];
            else if (args[i].StartsWith("--from=", StringComparison.Ordinal) && from is null) from = args[i]["--from=".Length..];
            else
            {
                await error.WriteLineAsync(UsageText);
                return Usage;
            }
        }
        if (string.IsNullOrWhiteSpace(from))
        {
            await error.WriteLineAsync(UsageText);
            return Usage;
        }
        if (!target.IsPostgres)
            return await RefuseAsync(error, "Set Database__Provider=Postgres and the PostgreSQL connection string (ConnectionStrings__Default or ConnectionStrings__Default_FILE), then run the copy again.");

        var path = Path.GetFullPath(from);
        if (!File.Exists(path)) return await RefuseAsync(error, $"No file at {path}.");

        try
        {
            return await CopyAsync(path, target, dataDir, output, error, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Types and SQL state only: messages can carry key values or name the server.
            await error.WriteLineAsync("Copy failed, nothing was committed: " + DatabaseErrors.Describe(e) + ".");
            return Failed;
        }
    }

    private static async Task<int> RefuseAsync(TextWriter error, string message)
    {
        await error.WriteLineAsync("Refused: " + message);
        return Refused;
    }

    private static async Task<int> CopyAsync(string path, DatabaseSetup target, string dataDir, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        // The app must be stopped (pentest R3-DEP-12b): writes made during the copy would be lost without a word.
        if (SqliteInUse(path))
            return await RefuseAsync(error, $"{path} is in use: another process (the app?) has it open. Stop the app, then copy.");

        var sourceConnection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var source = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel().UseSqlite(sourceConnection).Options);
        source.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        List<string> applied;
        try
        {
            applied = (await source.Database.GetAppliedMigrationsAsync(ct)).ToList();
        }
        catch (SqliteException)
        {
            return await RefuseAsync(error, $"{path} is not a readable SQLite database.");
        }
        var known = source.Database.GetMigrations().ToList();
        if (applied.Count == 0) return await RefuseAsync(error, $"{path} holds no Telrang database.");
        if (applied.Except(known).Any())
            return await RefuseAsync(error, "The SQLite database was written by a newer Telrang version. Copy it with that version.");
        if (known.Except(applied).Count() is var behind and > 0)
            return await RefuseAsync(error, $"The SQLite database is {behind} migration(s) behind this version. Start the old setup once with " +
                "this version (it migrates at startup), stop it, then copy.");

        var keys = Path.Combine(dataDir, "keys");
        if (!Directory.Exists(keys) || !Directory.EnumerateFiles(keys, "key-*.xml").Any())
        {
            await output.WriteLineAsync($"Warning: no Data Protection keys in {keys}. The app on PostgreSQL needs the keys of the SQLite " +
                "setup (same data directory or volume), or sign-ins, two-factor secrets, voter cookies, unsubscribe links and the stored " +
                "SMTP and captcha secrets stop working.");
        }

        var targetOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel();
        target.Apply(targetOptions);
        await using var db = new PostgresDbContext(targetOptions.Options);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        // Before migrating (pentest R3-DEP-12a): a database of another application gets no Telrang tables.
        if (await ForeignTargetAsync(db, ct) is { } foreign) return await RefuseAsync(error, foreign);
        await output.WriteLineAsync("Applying the PostgreSQL migrations.");
        await DatabaseSetup.MigrateAsync(db, ct);

        var tables = CopyOrder(db.Model);
        foreach (var t in tables)
        {
            if (await CountAsync(db, t, ct) > 0)
                return await RefuseAsync(error, $"The PostgreSQL database already holds Telrang rows (table {t.GetTableName()}). Copy into an " +
                    "empty database: drop and create it again, and do not start the app on it before the copy.");
        }

        var counts = new List<(string Table, long Rows)>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var copy = typeof(CopyToPostgresCommand).GetMethod(nameof(CopyTableAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var t in tables)
            {
                await output.WriteLineAsync($"Copying {t.GetTableName()}.");
                await (Task)copy.MakeGenericMethod(t.ClrType).Invoke(null, [source, db, ct])!;
            }
            await ResetSequencesAsync(db, tables, ct);

            var mismatch = false;
            foreach (var t in tables)
            {
                var expected = await CountAsync(source, t, ct);
                var actual = await CountAsync(db, t, ct);
                counts.Add((t.GetTableName()!, actual));
                if (expected == actual) continue;
                mismatch = true;
                await error.WriteLineAsync($"Count differs for {t.GetTableName()}: {expected} in SQLite, {actual} in PostgreSQL.");
            }
            if (mismatch)
            {
                await tx.RollbackAsync(ct);
                await error.WriteLineAsync("Copy failed, nothing was committed.");
                return Failed;
            }
            await tx.CommitAsync(ct);
        }

        await output.WriteLineAsync("Copied and verified (rows per table):");
        foreach (var (table, rows) in counts) await output.WriteLineAsync($"  {table}: {rows}");
        await output.WriteLineAsync($"Done. {path} is unchanged; keep it until the app runs well on PostgreSQL.");
        return Ok;
    }

    /// <summary>
    /// The SQLite file is open in another process. A database in WAL mode (EF's default) keeps a shared lock while any
    /// connection is open, so taking the exclusive lock for a moment fails then. Nothing is written. A file this account may
    /// not open for writing is not checked (a read-only copy is not in use by the app).
    /// </summary>
    public static bool SqliteInUse(string path) => SharedMemoryInUse(path + "-shm") || DatabaseLocked(path);

    /// <summary>
    /// In WAL mode every open connection, idle ones included, holds a read lock on the "dead man switch" byte (offset 128)
    /// of the <c>-shm</c> file for as long as it is open; a write lock on it fails while another process has the database
    /// open. The lock is taken and released at once; nothing is written.
    /// </summary>
    private static bool SharedMemoryInUse(string shm)
    {
        if (!File.Exists(shm) || OperatingSystem.IsMacOS()) return false;
        try
        {
            using var stream = new FileStream(shm, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            stream.Lock(128, 1);
            stream.Unlock(128, 1);
            return false;
        }
        catch (IOException) when (File.Exists(shm))
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool DatabaseLocked(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString();
        try
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; ROLLBACK;";
            command.ExecuteNonQuery();
            return false;
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) // SQLITE_BUSY, SQLITE_LOCKED
        {
            return true;
        }
        catch (SqliteException)
        {
            return false; // read-only file or folder: the read below reports anything else
        }
    }

    /// <summary>
    /// Why the target is not a database for Telrang, or null: it holds tables Telrang does not have, or a migration
    /// history with ids that are not Telrang's PostgreSQL migrations. A database that does not exist yet passes.
    /// </summary>
    private static async Task<string?> ForeignTargetAsync(PostgresDbContext db, CancellationToken ct)
    {
        if (!await db.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().ExistsAsync(ct)) return null;
        const string history = "__EFMigrationsHistory";
        var ours = db.Model.GetEntityTypes().Select(t => t.GetTableName()).OfType<string>().Append(history).Append("__EFMigrationsLock")
            .ToHashSet(StringComparer.Ordinal);
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = current_schema()").ToListAsync(ct);
        var foreign = tables.Where(t => !ours.Contains(t)).Order(StringComparer.Ordinal).ToList();
        if (foreign.Count > 0)
            return $"The PostgreSQL database holds tables that are not Telrang's ({string.Join(", ", foreign.Take(5))}{(foreign.Count > 5 ? ", ..." : "")}). " +
                "Copy into an empty database of its own.";
        if (!tables.Contains(history)) return null;
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await db.Database.SqlQueryRaw<string>($"SELECT \"MigrationId\" AS \"Value\" FROM \"{history}\"").ToListAsync(ct);
        return applied.Any(id => !known.Contains(id))
            ? "The PostgreSQL database has a migration history that is not this Telrang version's. Copy into an empty database of its own."
            : null;
    }

    /// <summary>Every table-mapped entity type (owned types travel with their owner), principals before dependents.</summary>
    public static IReadOnlyList<IEntityType> CopyOrder(IModel model)
    {
        var types = model.GetEntityTypes().Where(t => !t.IsOwned() && t.GetTableName() is not null)
            .OrderBy(t => t.GetTableName(), StringComparer.Ordinal).ToList();
        var ordered = new List<IEntityType>();
        var done = new HashSet<IEntityType>();
        while (ordered.Count < types.Count)
        {
            var next = types.FirstOrDefault(t => !done.Contains(t) && t.GetForeignKeys()
                .Select(fk => fk.PrincipalEntityType).All(p => p == t || done.Contains(p) || !types.Contains(p)))
                ?? throw new InvalidOperationException("The model has a foreign key cycle.");
            ordered.Add(next);
            done.Add(next);
        }
        return ordered;
    }

    /// <summary>Row count of an entity's table. The table name comes from the model (quoted), never from input.</summary>
    private static Task<long> CountAsync(DbContext db, IEntityType t, CancellationToken ct)
    {
        var sql = "SELECT COUNT(*) AS \"Value\" FROM " + Quote(t.GetTableName()!);
        return db.Database.SqlQueryRaw<long>(sql).SingleAsync(ct);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task CopyTableAsync<T>(ApplicationDbContext source, ApplicationDbContext target, CancellationToken ct) where T : class
    {
        var batch = new List<T>(BatchSize);
        await foreach (var row in source.Set<T>().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            batch.Add(row);
            if (batch.Count < BatchSize) continue;
            await SaveAsync(target, batch, ct);
        }
        if (batch.Count > 0) await SaveAsync(target, batch, ct);
    }

    private static async Task SaveAsync<T>(ApplicationDbContext target, List<T> batch, CancellationToken ct) where T : class
    {
        target.Set<T>().AddRange(batch);
        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear();
        batch.Clear();
    }

    /// <summary>Identity columns continue after the copied ids.</summary>
    private static async Task ResetSequencesAsync(ApplicationDbContext db, IEnumerable<IEntityType> tables, CancellationToken ct)
    {
        foreach (var t in tables)
        {
            foreach (var p in t.GetProperties().Where(p => p.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd
                         && (p.ClrType == typeof(int) || p.ClrType == typeof(long)) && p.IsPrimaryKey()))
            {
                var table = Quote(t.GetTableName()!);
                var column = p.GetColumnName();
                var literalTable = "'" + table.Replace("'", "''", StringComparison.Ordinal) + "'";
                var literalColumn = "'" + column.Replace("'", "''", StringComparison.Ordinal) + "'";
                var sql = "SELECT setval(pg_get_serial_sequence(" + literalTable + ", " + literalColumn + "), COALESCE(MAX(" + Quote(column) +
                    "), 1), MAX(" + Quote(column) + ") IS NOT NULL) FROM " + table;
                await db.Database.ExecuteSqlRawAsync(sql, ct);
            }
        }
    }
}
