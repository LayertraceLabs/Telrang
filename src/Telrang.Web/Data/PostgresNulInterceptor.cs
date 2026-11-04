using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Telrang.Web.Data;

/// <summary>
/// PostgreSQL text cannot hold U+0000; SQLite can. A NUL in any string parameter (a voter name, a search, a sign-in
/// email) would make the statement fail, so it is replaced by U+FFFD before the command runs: a request with a NUL
/// behaves as on SQLite instead of ending in a 500 (DESIGN.md §28). Values from the app's own code never contain one.
/// </summary>
public sealed class PostgresNulInterceptor : DbCommandInterceptor
{
    public static readonly PostgresNulInterceptor Instance = new();

    private PostgresNulInterceptor() { }

    public static string Clean(string value) => value.Contains('\0') ? value.Replace('\0', '�') : value;

    private static void Scrub(DbCommand command)
    {
        foreach (DbParameter p in command.Parameters)
        {
            switch (p.Value)
            {
                case string s when s.Contains('\0'):
                    p.Value = Clean(s);
                    break;
                case string?[] list when list.Any(s => s is not null && s.Contains('\0')):
                    p.Value = list.Select(s => s is null ? null : Clean(s)).ToArray();
                    break;
                case List<string> list when list.Any(s => s.Contains('\0')):
                    p.Value = list.Select(Clean).ToList();
                    break;
            }
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Scrub(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Scrub(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Scrub(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Scrub(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Scrub(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Scrub(command);
        return ValueTask.FromResult(result);
    }
}
