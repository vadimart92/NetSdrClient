using Microsoft.Extensions.Logging;

namespace NetSdr.Examples.Vega.Tests;

/// <summary>
/// Records the event id and the exception of every log entry of every logger it creates. The Vega test project has no
/// logging test package; the abstractions come with NetSdr.
/// </summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly Lock _sync = new();
    private readonly List<(int EventId, Exception? Exception)> _entries = [];

    /// <summary>A snapshot of the entries so far, oldest first.</summary>
    public IReadOnlyList<(int EventId, Exception? Exception)> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(RecordingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._sync)
            {
                owner._entries.Add((eventId.Id, exception));
            }
        }
    }
}
