using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace NutriFlow.Api.Tests;

internal sealed class TestErrorLogProvider : ILoggerProvider
{
    private const int MaximumEntries = 128;
    private readonly ConcurrentQueue<ErrorEntry> _entries = new();

    public ILogger CreateLogger(string categoryName)
    {
        return new ErrorLogger(categoryName, _entries);
    }

    public string DescribeErrors()
    {
        StringBuilder description = new();
        foreach (ErrorEntry entry in _entries.ToArray())
        {
            description.AppendLine($"{entry.Level}: {entry.Category} [{entry.EventId}]");
            description.AppendLine(entry.Exception?.ToString() ?? "No exception was attached.");
            if (entry.Exception is not null)
            {
                AppendSqliteCodes(description, entry.Exception);
            }
        }

        return description.Length == 0 ? "No server errors were recorded." : description.ToString();
    }

    public void Dispose()
    {
    }

    private static void AppendSqliteCodes(StringBuilder description, Exception exception)
    {
        if (exception is SqliteException sqlite)
        {
            description.AppendLine($"SqliteErrorCode: {sqlite.SqliteErrorCode}; SqliteExtendedErrorCode: {sqlite.SqliteExtendedErrorCode}");
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                AppendSqliteCodes(description, inner);
            }
        }
        else if (exception.InnerException is not null)
        {
            AppendSqliteCodes(description, exception.InnerException);
        }
    }

    private sealed record ErrorEntry(LogLevel Level, string Category, EventId EventId, Exception? Exception);

    private sealed class ErrorLogger(string category, ConcurrentQueue<ErrorEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Error or LogLevel.Critical;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            entries.Enqueue(new ErrorEntry(logLevel, category, eventId, exception));
            while (entries.Count > MaximumEntries)
            {
                entries.TryDequeue(out _);
            }
        }
    }
}
