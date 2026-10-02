namespace NetSdr.Data;

/// <summary>Estimates of the data-channel throughput.</summary>
public static class DataRate
{
    private const int Int16BytesPerSample = 4;
    private const int Int24BytesPerSample = 6;

    /// <summary>
    /// Bytes per second of sample data: <paramref name="sampleRate"/> times the size of one complex sample
    /// (4 bytes for <see cref="SampleFormat.Int16"/>, 6 for <see cref="SampleFormat.Int24"/>) times
    /// <paramref name="channels"/>, rounded up.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="format"/> is <see cref="SampleFormat.Unknown"/>.</exception>
    public static long BytesPerSecond(double sampleRate, SampleFormat format, int channels = 1)
    {
        int bytesPerSample = format switch
        {
            SampleFormat.Int16 => Int16BytesPerSample,
            SampleFormat.Int24 => Int24BytesPerSample,
            _ => throw new ArgumentException("The sample format must be Int16 or Int24.", nameof(format)),
        };

        return (long)Math.Ceiling(sampleRate * bytesPerSample * channels);
    }
}
