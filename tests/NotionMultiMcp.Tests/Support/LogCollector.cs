using Microsoft.Extensions.Logging;

namespace NotionMultiMcp.Tests.Support;

internal sealed record LogEntry(LogLevel Level, string Category, string Message);

/// <summary>A logger provider that keeps every entry in memory.</summary>
internal sealed class LogCollector : ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CollectingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CollectingLogger(LogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._gate)
            {
                owner._entries.Add(new LogEntry(logLevel, category, formatter(state, exception)));
            }
        }
    }
}
