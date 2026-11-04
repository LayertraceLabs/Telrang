using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Telrang.Web.Data;

public enum DatabaseProvider { Sqlite, Postgres }

/// <summary>Exit codes of a start that stops before serving (DESIGN.md §28).</summary>
public static class StartupExit
{
    /// <summary>Database provider or connection string configuration is unusable.</summary>
    public const int Configuration = 2;

    /// <summary>The database cannot be opened or migrated.</summary>
    public const int Database = 3;
}

/// <summary>A database configuration the app cannot start with. The message never holds the connection string.</summary>
public sealed class DatabaseConfigException(string message) : Exception(message);

/// <summary>
/// The database the app runs on (DESIGN.md §28), from the environment only: <c>Database__Provider=Sqlite|Postgres</c>
/// (default Sqlite) and <c>ConnectionStrings__Default</c>, or <c>ConnectionStrings__Default_FILE</c> naming a file that
/// holds it (Docker secrets; the file wins). Never from the admin panel or the database. <see cref="ToString"/> and every
/// error name the provider only: the connection string is never logged, shown or put into an exception message.
/// </summary>
public sealed class DatabaseSetup
{
    public const string ProviderKey = "Database:Provider";
    public const string ConnectionStringKey = "ConnectionStrings:Default";
    public const string ConnectionStringFileKey = "ConnectionStrings:Default_FILE";

    private DatabaseSetup(DatabaseProvider provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public DatabaseProvider Provider { get; }

    /// <summary>The connection string. Only for <see cref="Apply"/> and the copy command; never log or show it.</summary>
    public string ConnectionString { get; }

    public bool IsPostgres => Provider == DatabaseProvider.Postgres;

    /// <summary>"PostgreSQL" or "SQLite": all the Status tab and the logs say about the database.</summary>
    public string DisplayName => IsPostgres ? "PostgreSQL" : "SQLite";

    public override string ToString() => DisplayName;

    /// <summary>Reads the configuration; throws <see cref="DatabaseConfigException"/> when the app cannot start with it.</summary>
    /// <param name="dataDir">The data directory (SQLite's default file is <c>app.db</c> in it).</param>
    public static DatabaseSetup FromConfiguration(IConfiguration config, string dataDir)
    {
        var providerText = config[ProviderKey];
        DatabaseProvider provider;
        if (string.IsNullOrWhiteSpace(providerText) || providerText.Trim().Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            provider = DatabaseProvider.Sqlite;
        else if (providerText.Trim().Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            provider = DatabaseProvider.Postgres;
        else
            throw new DatabaseConfigException("Database__Provider must be Sqlite or Postgres.");

        var connectionString = ReadConnectionString(config);
        if (provider == DatabaseProvider.Sqlite)
            return new DatabaseSetup(provider, connectionString ?? $"Data Source={Path.Combine(dataDir, "app.db")}");

        if (connectionString is null)
            throw new DatabaseConfigException(
                "Database__Provider=Postgres needs ConnectionStrings__Default or ConnectionStrings__Default_FILE.");
        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            // The parser's message can quote a part of the string: not passed on.
            throw new DatabaseConfigException("The PostgreSQL connection string cannot be read. Check its keywords and values.");
        }
        // Npgsql's own default (100) is above the slots a default server leaves for non-superusers (97): waiting writes
        // would take the server's last connections (pentest R3-DEP-1). An explicit Maximum Pool Size wins.
        if (!SetsPoolSize(connectionString))
        {
            parsed.MaxPoolSize = DefaultMaxPoolSize;
            connectionString = parsed.ConnectionString;
        }
        return new DatabaseSetup(provider, connectionString) { MaxPoolSize = parsed.MaxPoolSize };
    }

    /// <summary>The PostgreSQL pool size when the connection string does not set <c>Maximum Pool Size</c>.</summary>
    public const int DefaultMaxPoolSize = 30;

    /// <summary>PostgreSQL: the connection pool's maximum (connection string, else <see cref="DefaultMaxPoolSize"/>); 0 on SQLite.</summary>
    public int MaxPoolSize { get; private init; }

    private static bool SetsPoolSize(string connectionString)
    {
        var keys = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }.Keys.Cast<string>();
        return keys.Any(k => k.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() is "maximumpoolsize" or "maxpoolsize");
    }

    /// <summary>
    /// PostgreSQL: a warning when the pool may open more connections than the server leaves for non-superusers
    /// (<c>max_connections</c> minus the reserved slots), else null. Then waiting writes can take every slot and new
    /// connections, reads and <c>/healthz</c> included, fail (pentest R3-DEP-1).
    /// </summary>
    public async Task<string?> PoolProblemAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (!IsPostgres) return null;
        var slots = await db.Database.SqlQueryRaw<int>(
            "SELECT current_setting('max_connections')::int - current_setting('superuser_reserved_connections')::int " +
            "- COALESCE(NULLIF(current_setting('reserved_connections', true), '')::int, 0) AS \"Value\"").SingleAsync(ct);
        return MaxPoolSize > slots
            ? $"The PostgreSQL connection pool (Maximum Pool Size {MaxPoolSize}) is larger than the {slots} connections the server " +
              "leaves for non-superusers. Set Maximum Pool Size in the connection string below that, or raise max_connections."
            : null;
    }

    /// <summary>The file named by <c>ConnectionStrings__Default_FILE</c> when set, else <c>ConnectionStrings__Default</c>; null when empty.</summary>
    private static string? ReadConnectionString(IConfiguration config)
    {
        var file = config[ConnectionStringFileKey];
        if (!string.IsNullOrWhiteSpace(file))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new DatabaseConfigException("ConnectionStrings__Default_FILE names a file that cannot be read.");
            }
            var value = text.Trim();
            return value.Length > 0 ? value : throw new DatabaseConfigException("ConnectionStrings__Default_FILE names an empty file.");
        }
        var direct = config[ConnectionStringKey];
        return string.IsNullOrWhiteSpace(direct) ? null : direct.Trim();
    }

    /// <summary>Configures EF Core for this provider (also used by the copy command).</summary>
    public void Apply(DbContextOptionsBuilder options)
    {
        if (IsPostgres)
        {
            options.UseNpgsql(ConnectionString)
                .AddInterceptors(PostgresNulInterceptor.Instance)
                // EF's connection events name the server and database: never logged (DESIGN.md §28). Failures still
                // reach the caller, and their log lines go through DatabaseErrors' redaction.
                .ConfigureWarnings(w => w.Ignore(
                    RelationalEventId.ConnectionOpening, RelationalEventId.ConnectionOpened,
                    RelationalEventId.ConnectionClosing, RelationalEventId.ConnectionClosed,
                    RelationalEventId.ConnectionDisposing, RelationalEventId.ConnectionDisposed,
                    RelationalEventId.ConnectionError, RelationalEventId.ConnectionCanceled,
                    RelationalEventId.MigrateUsingConnection));
        }
        else
        {
            options.UseSqlite(ConnectionString);
        }
    }

    /// <summary>
    /// Applies the migrations of the context's provider. On PostgreSQL the history table is created first in an existing
    /// database: EF otherwise reads it before creating it and logs that expected failure as an error. A missing database
    /// is created by EF when the role may (CREATEDB).
    /// </summary>
    public static async Task MigrateAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql() && await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct))
            await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(ct);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>A context of the type whose migration set matches the provider (<see cref="PostgresDbContext"/> on PostgreSQL).</summary>
    public ApplicationDbContext CreateContext(DbContextOptions<ApplicationDbContext> options) =>
        IsPostgres ? new PostgresDbContext(options) : new ApplicationDbContext(options);
}

/// <summary>
/// <see cref="IDbContextFactory{ApplicationDbContext}"/> for both providers: on PostgreSQL it hands out
/// <see cref="PostgresDbContext"/>, the same model with its own migration set (DESIGN.md §28).
/// </summary>
public sealed class AppDbContextFactory(DbContextOptions<ApplicationDbContext> options, DatabaseSetup setup) : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => setup.CreateContext(options);
}

/// <summary>
/// Identity builds its tables from <c>IdentityOptions.Stores.SchemaVersion</c>, read from the application services. A
/// context created outside the app's DI (design time, <c>copy-to-postgres</c>, tests) gets these services so its model
/// matches the app's (version 3: passkeys).
/// </summary>
public static class IdentityModel
{
    private static readonly Lazy<IServiceProvider> Services = new(() => new ServiceCollection()
        .Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(o => o.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3)
        .BuildServiceProvider());

    public static DbContextOptionsBuilder<TContext> UseAppIdentityModel<TContext>(this DbContextOptionsBuilder<TContext> options)
        where TContext : DbContext =>
        options.UseApplicationServiceProvider(Services.Value);
}
