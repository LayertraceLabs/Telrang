namespace Telrang.Web.Tests;

/// <summary>
/// A test about SQLite itself (its migration history, the app.db file, VACUUM INTO backups): skipped when the suite runs
/// against PostgreSQL (<c>TELRANG_TEST_POSTGRES</c>, DESIGN.md §28). PostgreSQL has its own tests for the same concerns.
/// </summary>
public sealed class SqliteOnlyFactAttribute : FactAttribute
{
    public SqliteOnlyFactAttribute()
    {
        if (AppFactory.OnPostgres) Skip = "SQLite only; the suite runs on PostgreSQL (TELRANG_TEST_POSTGRES).";
    }
}

/// <summary>A theory that needs a PostgreSQL server, like <see cref="PostgresFactAttribute"/>.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!AppFactory.OnPostgres) Skip = "Needs TELRANG_TEST_POSTGRES (a PostgreSQL server where tests may create databases).";
    }
}

/// <summary>Needs a PostgreSQL server: skipped unless <c>TELRANG_TEST_POSTGRES</c> is set (DESIGN.md §28).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!AppFactory.OnPostgres) Skip = "Needs TELRANG_TEST_POSTGRES (a PostgreSQL server where tests may create databases).";
    }
}
