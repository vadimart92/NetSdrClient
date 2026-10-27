namespace NetSdr.Data;

/// <summary>The time base of a sample clock: how sample ticks relate to the 100-nanosecond ticks of <see cref="TimeSpan"/>.</summary>
public readonly struct SampleRateTimeScale
{
    /// <param name="sampleRate">The sample rate in hertz.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> is not positive and finite.</exception>
    public SampleRateTimeScale(double sampleRate)
    {
        if (!double.IsFinite(sampleRate) || sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "The sample rate must be positive.");
        }

        SampleRate = sampleRate;
    }

    /// <summary>The sample rate in hertz.</summary>
    public double SampleRate { get; }

    /// <summary>Sample ticks per 100 nanoseconds: the sample rate divided by 10 000 000.</summary>
    public double TicksPer100Nanoseconds => SampleRate / TimeSpan.TicksPerSecond;
}
