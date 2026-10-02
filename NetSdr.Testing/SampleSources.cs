using NetSdr.Data;

namespace NetSdr.Testing;

/// <summary>
/// Sample generators for <see cref="StreamOptions.Source"/>. Each fills whole complex samples and zeros the bytes
/// left over when the destination is not a whole number of samples.
/// </summary>
public static class SampleSources
{
    private const double FullScale16 = 32767;
    private const double FullScale24 = 8388607;

    /// <summary>
    /// Plays <paramref name="samples"/> over and over: the sample with index <c>k</c> starts at byte
    /// <c>k * bytesPerSample mod length</c>. The memory is read on every call and not copied.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is empty.</exception>
    public static FillSamples FromBuffer(ReadOnlyMemory<byte> samples)
    {
        if (samples.IsEmpty)
        {
            throw new ArgumentException("The buffer must not be empty.", nameof(samples));
        }

        return (destination, firstSampleIndex, format) =>
        {
            Span<byte> whole = WholeSamples(destination, BytesPerSample(format));
            ReadOnlySpan<byte> buffer = samples.Span;
            long position = firstSampleIndex * BytesPerSample(format) % buffer.Length;
            if (position < 0)
            {
                position += buffer.Length;
            }

            while (!whole.IsEmpty)
            {
                ReadOnlySpan<byte> chunk = buffer[(int)position..];
                int count = Math.Min(chunk.Length, whole.Length);
                chunk[..count].CopyTo(whole);
                whole = whole[count..];
                position = 0;
            }
        };
    }

    /// <summary>
    /// A complex tone: for the sample with index <c>idx</c> the phase is <c>phi = 2 * pi * frequencyHz * idx / sampleRate</c>,
    /// <c>I = round(amplitude * F * cos(phi))</c> and <c>Q = round(amplitude * F * sin(phi))</c>, with <c>F</c> the full scale
    /// of the format, 32767 for 16 bits and 8388607 for 24.
    /// </summary>
    /// <param name="frequencyHz">The tone frequency; negative turns the phase the other way.</param>
    /// <param name="sampleRate">The sample rate the tone is computed for, in hertz.</param>
    /// <param name="amplitude">The share of full scale, from 0 to 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The frequency is not finite, the sample rate is not positive and finite, or the amplitude is outside 0..1.
    /// </exception>
    public static FillSamples Tone(double frequencyHz, double sampleRate, double amplitude)
    {
        if (!double.IsFinite(frequencyHz))
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "The frequency must be finite.");
        }

        if (!double.IsFinite(sampleRate) || sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "The sample rate must be positive.");
        }

        if (!(amplitude >= 0 && amplitude <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "The amplitude must be between 0 and 1.");
        }

        double cyclesPerSample = frequencyHz / sampleRate;
        return (destination, firstSampleIndex, format) =>
        {
            int component = BytesPerSample(format) / 2;
            double scale = amplitude * (component == 2 ? FullScale16 : FullScale24);
            Span<byte> samples = WholeSamples(destination, component * 2);
            long index = firstSampleIndex;
            for (int offset = 0; offset < samples.Length; offset += component * 2, index++)
            {
                // Only the fraction of a cycle matters, and it keeps its precision where the index is large.
                double cycles = cyclesPerSample * index;
                double phase = 2 * Math.PI * (cycles - Math.Floor(cycles));
                WriteLow(samples.Slice(offset, component), (long)Math.Round(scale * Math.Cos(phase), MidpointRounding.AwayFromZero));
                WriteLow(samples.Slice(offset + component, component), (long)Math.Round(scale * Math.Sin(phase), MidpointRounding.AwayFromZero));
            }
        };
    }

    /// <summary>
    /// A counter for exact assertions: for the sample with index <c>idx</c>, <c>I = idx</c> and <c>Q = ~idx</c>, each
    /// written as its low 2 or 3 bytes, little-endian.
    /// </summary>
    public static FillSamples Counter() => (destination, firstSampleIndex, format) =>
    {
        int component = BytesPerSample(format) / 2;
        Span<byte> samples = WholeSamples(destination, component * 2);
        long index = firstSampleIndex;
        for (int offset = 0; offset < samples.Length; offset += component * 2, index++)
        {
            WriteLow(samples.Slice(offset, component), index);
            WriteLow(samples.Slice(offset + component, component), ~index);
        }
    };

    /// <summary>The size of one complex sample: 4 bytes for 16-bit components and 6 for 24-bit ones.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is neither <see cref="SampleFormat.Int16"/> nor <see cref="SampleFormat.Int24"/>.</exception>
    internal static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.Int16 => 4,
        SampleFormat.Int24 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The sample format must be Int16 or Int24."),
    };

    /// <summary>The part of <paramref name="destination"/> that holds whole samples; the rest is cleared.</summary>
    private static Span<byte> WholeSamples(Span<byte> destination, int bytesPerSample)
    {
        int whole = destination.Length / bytesPerSample * bytesPerSample;
        destination[whole..].Clear();
        return destination[..whole];
    }

    /// <summary>Writes the low <c>destination.Length</c> bytes of <paramref name="value"/>, little-endian.</summary>
    private static void WriteLow(Span<byte> destination, long value)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(value >> (8 * i));
        }
    }
}
