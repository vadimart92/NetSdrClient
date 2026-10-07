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
            if (clock.Elapsed > Limits.Test)
                throw new TimeoutException($"The condition was not met within {Limits.Test} of real time.");
            time.Advance(step);
            await Task.Delay(1);
        }
    }
}

/// <summary>
/// Forwards everything to <c>inner</c> and records the name of the thread behind every <see cref="GetTimestamp"/>
/// call and the due time of every timer, so a test can count how often, and where, a component reads the clock, and
/// wait until a timer exists before it moves fake time.
/// </summary>
internal sealed class CountingTimeProvider(TimeProvider inner) : TimeProvider
{
    private readonly ConcurrentQueue<string?> _timestampReaders = new();
    private readonly ConcurrentQueue<TimeSpan> _timerDueTimes = new();

    /// <summary>The thread name of each <see cref="GetTimestamp"/> call, in call order.</summary>
    public IReadOnlyCollection<string?> TimestampReaders => _timestampReaders;

    /// <summary>The due time of each timer, in creation order; recorded once <c>inner</c> has created it.</summary>
    public IReadOnlyCollection<TimeSpan> TimerDueTimes => _timerDueTimes;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long GetTimestamp()
    {
        long timestamp = inner.GetTimestamp();
        // After the read: whoever sees the entry can move fake time without changing what this call returns.
        _timestampReaders.Enqueue(Thread.CurrentThread.Name);
        return timestamp;
    }

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
        // After the creation: whoever sees the entry can move fake time, and the timer is already counting.
        _timerDueTimes.Enqueue(dueTime);
        return timer;
    }
}
