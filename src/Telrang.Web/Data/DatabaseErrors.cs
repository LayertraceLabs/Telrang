using System.Data.Common;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Telrang.Web.Data;

/// <summary>
/// Keeps PostgreSQL connection details out of every log line (DESIGN.md §28). Npgsql's messages can name the host, port,
/// database or user ("Failed to connect to …", "password authentication failed for user …"); an exception with an
/// Npgsql exception anywhere in its chain is logged as <see cref="RedactedDatabaseException"/>: the exception types and
/// the SQL state, plus the outer stack trace. Applied to every logger through <see cref="RedactingLoggerFactory"/>.
/// </summary>
public static class DatabaseErrors
{
    private static readonly System.Reflection.Assembly Npgsql = typeof(global::Npgsql.NpgsqlException).Assembly;

    /// <summary>All exceptions in the chain, outermost first (inner exceptions and aggregates).</summary>
    public static IEnumerable<Exception> Chain(Exception e)
    {
        var pending = new Stack<Exception>();
        pending.Push(e);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 32)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException agg)
                foreach (var inner in agg.InnerExceptions.Reverse()) pending.Push(inner);
            else if (current.InnerException is { } inner)
                pending.Push(inner);
        }
    }

    /// <summary>The chain holds an exception thrown by Npgsql (the PostgreSQL driver).</summary>
    public static bool FromPostgres(Exception e) => Chain(e).Any(x => x.GetType().Assembly == Npgsql);

    /// <summary>A database error in the chain (Npgsql or Microsoft.Data.Sqlite, or EF's wrappers of them).</summary>
    public static bool IsDatabaseError(Exception e) => Chain(e).Any(x => x is DbException || x.GetType().Assembly == Npgsql);

    /// <summary>"DbUpdateException > PostgresException (SQL state 23505)": types and SQL state, nothing else.</summary>
    public static string Describe(Exception e)
    {
        var chain = Chain(e).ToList();
        var state = chain.OfType<DbException>().Select(d => d.SqlState).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        var types = string.Join(" > ", chain.Select(x => x.GetType().Name));
        return state is null ? types : $"{types} (SQL state {state})";
    }

    /// <summary>The exception to log: <paramref name="e"/> itself, or a redacted stand-in when Npgsql is in its chain.</summary>
    public static Exception? Redact(Exception? e) => e is null || e is RedactedDatabaseException || !FromPostgres(e)
        ? e
        : new RedactedDatabaseException(e);

    /// <summary>
    /// <paramref name="text"/> with the text of every exception in <paramref name="e"/>'s chain (its <c>ToString()</c> and
    /// its message) replaced by <see cref="Describe"/>; for log messages that quote a PostgreSQL driver exception.
    /// </summary>
    public static string RedactText(string text, Exception e)
    {
        var description = "[" + Describe(e) + "; details withheld: they can name the server]";
        foreach (var x in Chain(e).ToList())
        {
            foreach (var part in new[] { x.ToString(), x.Message })
            {
                if (part.Length > 0) text = text.Replace(part, description, StringComparison.Ordinal);
            }
            if (x is global::Npgsql.PostgresException pg)
            {
                foreach (var part in new[] { pg.MessageText, pg.Detail, pg.Hint })
                {
                    if (!string.IsNullOrEmpty(part)) text = text.Replace(part, "[withheld]", StringComparison.Ordinal);
                }
            }
        }
        return text;
    }

    /// <summary>Wraps the app's <see cref="ILoggerFactory"/> so every logger redacts database exceptions.</summary>
    public static IServiceCollection AddDatabaseErrorRedaction(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new RedactingLoggerFactory(new LoggerFactory(
            sp.GetServices<ILoggerProvider>(),
            sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
            sp.GetService<IOptions<LoggerFactoryOptions>>(),
            sp.GetService<IExternalScopeProvider>()))));
        return services;
    }
}

/// <summary>Stands in for a PostgreSQL driver exception in logs: types and SQL state only, the original stack trace.</summary>
public sealed class RedactedDatabaseException(Exception original)
    : Exception("Database error: " + DatabaseErrors.Describe(original) + " (details withheld: they can name the server)")
{
    private readonly string? stack = original.StackTrace;

    public override string? StackTrace => stack;
}

/// <summary>The default logger factory, with every logger passing exceptions through <see cref="DatabaseErrors.Redact"/>.</summary>
public sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var redacted = DatabaseErrors.Redact(exception);
            if (exception is null || ReferenceEquals(redacted, exception))
            {
                inner.Log(logLevel, eventId, state, redacted, (s, _) => formatter(s, redacted));
                return;
            }
            // EF's own events (QueryIterationFailed, SaveChangesFailed, …) also put the exception's text into the message
            // (pentest R3-DEP-5): the message is logged with that text replaced, and without the structured state, which
            // holds it too.
            var message = DatabaseErrors.RedactText(formatter(state, exception), exception);
            inner.Log(logLevel, eventId, message, redacted, (s, _) => s);
        }
    }
}
