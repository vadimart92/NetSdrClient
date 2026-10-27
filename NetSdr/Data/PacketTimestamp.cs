using System.Buffers.Binary;

namespace NetSdr.Data;

/// <summary>
/// The time of the first sample of a data packet, carried in the last <see cref="Size"/> bytes of the datagram by
/// devices that stamp their packets: <see cref="Ticks"/> then <see cref="UtcTimestampSeconds"/>, each a little-endian
/// <see cref="uint"/>. The time is the whole UTC second plus <see cref="Ticks"/> sample periods.
/// </summary>
/// <param name="Ticks">Sample ticks since the start of the second, at the sample rate of the stream.</param>
/// <param name="UtcTimestampSeconds">The UTC second, in seconds since the Unix epoch.</param>
public readonly record struct PacketTimestamp(uint Ticks, uint UtcTimestampSeconds)
{
    /// <summary>The bytes the timestamp takes on the wire.</summary>
    public const int Size = 8;

    /// <summary>The time the timestamp stands for, to the nearest 100 nanoseconds.</summary>
    /// <param name="scale">The time base of the stream's sample clock.</param>
    public DateTimeOffset ToDateTimeOffset(SampleRateTimeScale scale) =>
        DateTimeOffset
            .FromUnixTimeSeconds(UtcTimestampSeconds)
            .AddTicks((long)Math.Round(Ticks / scale.TicksPer100Nanoseconds, MidpointRounding.AwayFromZero));

    /// <summary>
    /// The timestamp of the sample <paramref name="sampleIndex"/> sample periods after <paramref name="origin"/>, the
    /// time of sample 0. Sample ticks are whole: a time between two ticks gets the earlier one.
    /// </summary>
    /// <param name="origin">The time of sample 0.</param>
    /// <param name="sampleIndex">The sample, counted from sample 0; not negative.</param>
    /// <param name="scale">The time base of the stream's sample clock.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleIndex"/> is negative.</exception>
    /// <exception cref="OverflowException">The second is before the Unix epoch or after 2106, beyond a <see cref="uint"/>.</exception>
    public static PacketTimestamp AtSample(DateTimeOffset origin, long sampleIndex, SampleRateTimeScale scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);

        // Decimal keeps the tick count exact for whole sample rates, however long the stream has been running.
        long sinceEpoch = origin.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        long second = Math.DivRem(sinceEpoch, TimeSpan.TicksPerSecond, out long within);
        if (within < 0)
        {
            second--;
            within += TimeSpan.TicksPerSecond;
        }

        decimal rate = (decimal)scale.SampleRate;
        decimal ticks = within * rate / TimeSpan.TicksPerSecond + sampleIndex;
        decimal seconds = Math.Floor(ticks / rate);
        return new PacketTimestamp(
            (uint)Math.Floor(ticks - seconds * rate),
            checked((uint)(second + (long)seconds)));
    }

    /// <summary>Reads a timestamp from the first <see cref="Size"/> bytes of <paramref name="source"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is shorter than <see cref="Size"/>.</exception>
    public static PacketTimestamp Read(ReadOnlySpan<byte> source)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(source.Length, Size, nameof(source));
        return new PacketTimestamp(
            BinaryPrimitives.ReadUInt32LittleEndian(source),
            BinaryPrimitives.ReadUInt32LittleEndian(source[sizeof(uint)..]));
    }

    /// <summary>
    /// Reads the timestamp from the end of the samples a <see cref="DataPacketHandler"/> is given; the samples proper
    /// are everything before the last <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="samples"/> is shorter than <see cref="Size"/>.</exception>
    public static PacketTimestamp ReadTrailer(ReadOnlySpan<byte> samples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples.Length, Size, nameof(samples));
        return Read(samples[^Size..]);
    }

    /// <summary>Writes the timestamp to the first <see cref="Size"/> bytes of <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destination"/> is shorter than <see cref="Size"/>.</exception>
    public void Write(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size, nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Ticks);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[sizeof(uint)..], UtcTimestampSeconds);
    }
}
