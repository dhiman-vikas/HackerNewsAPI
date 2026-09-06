using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RestfulAPIDemo.Tests.Fakes;

/// <summary>Collects every log entry the application writes so tests can assert on levels and messages.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public IEnumerable<LogEntry> AtLevel(LogLevel level) => _entries.Where(e => e.Level == level);

    public IEnumerable<LogEntry> FromCategory<T>() => _entries.Where(e => e.Category == typeof(T).FullName);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
    }
}

public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);
