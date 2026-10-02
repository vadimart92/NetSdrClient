using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>
/// Receiver frequency ranges, answered by the device to a range request for item 0x0020.
/// The payload is <c>[channel][count]</c> followed by <c>count</c> records of three <see cref="UInt40"/>
/// (minimum, maximum, VCO). The item is read-only: <c>Write</c> is not overridden, so writing it throws
/// <see cref="ArgumentException"/>.
/// </summary>
public readonly struct FrequencyRanges : IControlItem<FrequencyRanges>
{
    private const int HeaderSize = 2;
    private const int RecordSize = 15;

    private readonly FrequencyRange[]? _ranges;

    public static ushort Code => 0x0020;

    /// <exception cref="ArgumentNullException"><paramref name="ranges"/> is <see langword="null"/>.</exception>
    public FrequencyRanges(byte channel, FrequencyRange[] ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        Channel = channel;
        _ranges = ranges;
    }

    public byte Channel { get; }

    /// <summary>The bands the channel covers; empty for <c>default</c>.</summary>
    public FrequencyRange[] Ranges => _ranges ?? [];

    /// <summary>Reads the channel and the announced number of ranges; trailing bytes are ignored.</summary>
    /// <exception cref="ArgumentException">The payload is shorter than <c>2 + count * 15</c> bytes.</exception>
    public static FrequencyRanges Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"Frequency ranges need at least {HeaderSize} bytes but the payload holds {source.Length}.", nameof(source));
        }

        int count = source[1];
        int needed = HeaderSize + count * RecordSize;
        if (source.Length < needed)
        {
            throw new ArgumentException(
                $"{count} frequency ranges need {needed} bytes but the payload holds {source.Length}.", nameof(source));
        }

        var ranges = new FrequencyRange[count];
        for (int i = 0; i < count; i++)
        {
            var record = source.Slice(HeaderSize + i * RecordSize, RecordSize);
            ranges[i] = new FrequencyRange(
                MemoryMarshal.Read<UInt40>(record),
                MemoryMarshal.Read<UInt40>(record[5..]),
                MemoryMarshal.Read<UInt40>(record[10..]));
        }

        return new FrequencyRanges(source[0], ranges);
    }
}
