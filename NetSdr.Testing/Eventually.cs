using System.Diagnostics;

namespace NetSdr.Testing;

/// <summary>Polls a condition until it holds, so asynchronous tests wait for state without fixed sleeps.</summary>
public static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>Completes when <paramref name="condition"/> returns <see langword="true"/>, checking every 10 ms.</summary>
    /// <param name="condition">The condition to poll.</param>
    /// <param name="timeout">How long to wait; 5 seconds when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is zero, negative or <see cref="Timeout.InfiniteTimeSpan"/>: a wait in a test must end.
    /// </exception>
    /// <exception cref="TimeoutException">The condition did not become true within <paramref name="timeout"/>.</exception>
    public static async Task ThatAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (timeout is { } given && given <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), given, "The timeout must be positive; an infinite wait would hang the test.");
        }

        TimeSpan limit = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= limit)
            {
                throw new TimeoutException($"The condition was not met within {limit}.");
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }
}
