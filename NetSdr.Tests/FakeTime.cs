using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace NetSdr.Tests;

/// <summary>Helpers for driving a fake clock in tests.</summary>
internal static class FakeTime
{
    /// <summary>
    /// Advances fake time by <paramref name="step"/> until <paramref name="condition"/> holds. Pending timers of FakeTimeProvider
    /// cannot be seen, so time moves in steps with a real millisecond between them for the released work to run.
    /// </summary>
    /// <exception cref="TimeoutException">The condition did not hold within Limits.Test of real time.</exception>
    public static async Task AdvanceUntilAsync(this FakeTimeProvider time, Func<bool> condition, TimeSpan step)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Limits.Test) throw new TimeoutException($"The condition was not met within {Limits.Test} of real time.");
            time.Advance(step);
            await Task.Delay(1);
        }
    }
}

/// <summary>
/// Forwards everything to <c>inner</c> and records the name of the thread behind every
/// <see cref="GetTimestamp"/> call, so a test can count how often, and where, a component reads the clock.
/// </summary>
internal sealed class CountingTimeProvider(TimeProvider inner) : TimeProvider
{
    private readonly ConcurrentQueue<string?> _timestampReaders = new();

    /// <summary>The thread name of each <see cref="GetTimestamp"/> call, in call order.</summary>
    public IReadOnlyCollection<string?> TimestampReaders => _timestampReaders;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long GetTimestamp()
    {
        _timestampReaders.Enqueue(Thread.CurrentThread.Name);
        return inner.GetTimestamp();
    }

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}
