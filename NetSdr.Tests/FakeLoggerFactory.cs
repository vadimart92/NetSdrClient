using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace NetSdr.Tests;

/// <summary>Hands out FakeLoggers that share one collector; levels below <c>minimum</c> are disabled and not collected.</summary>
internal sealed class FakeLoggerFactory(LogLevel minimum = LogLevel.Trace) : ILoggerFactory
{
    public FakeLogCollector Collector { get; } =
        FakeLogCollector.Create(new FakeLogCollectorOptions { CollectRecordsForDisabledLogLevels = false });

    public IReadOnlyList<FakeLogRecord> Events(int id) => Collector.GetSnapshot().Where(r => r.Id.Id == id).ToList();

    public ILogger CreateLogger(string categoryName)
    {
        var logger = new FakeLogger(Collector, categoryName);
        for (var level = LogLevel.Trace; level < minimum; level++)
            logger.ControlLevel(level, false);
        return logger;
    }

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }
}

internal static class FakeLogRecordExtensions
{
    public static string? Value(this FakeLogRecord record, string key) => record.GetStructuredStateValue(key);
    public static TimeSpan Span(this FakeLogRecord record, string key) =>
        TimeSpan.Parse(record.Value(key)!, CultureInfo.InvariantCulture);
}
