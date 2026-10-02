using System.Globalization;
using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>
/// An unsigned 40-bit integer stored as five little-endian bytes, as used for frequencies on the wire.
/// Converts implicitly to and from <see cref="ulong"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct UInt40 : IEquatable<UInt40>
{
    /// <summary>The largest representable value, 2^40 - 1.</summary>
    public const ulong MaxValue = (1UL << 40) - 1;

    private readonly byte _b0;
    private readonly byte _b1;
    private readonly byte _b2;
    private readonly byte _b3;
    private readonly byte _b4;

    /// <exception cref="OverflowException"><paramref name="value"/> is above <see cref="MaxValue"/>.</exception>
    public UInt40(ulong value)
    {
        if (value > MaxValue)
        {
            throw new OverflowException($"The value {value} does not fit in 40 bits.");
        }

        _b0 = (byte)value;
        _b1 = (byte)(value >> 8);
        _b2 = (byte)(value >> 16);
        _b3 = (byte)(value >> 24);
        _b4 = (byte)(value >> 32);
    }

    public static implicit operator ulong(UInt40 value) => value.ToUInt64();

    /// <exception cref="OverflowException"><paramref name="value"/> is above <see cref="MaxValue"/>.</exception>
    public static implicit operator UInt40(ulong value) => new(value);

    public bool Equals(UInt40 other) => ToUInt64() == other.ToUInt64();

    public override bool Equals(object? obj) => obj is UInt40 other && Equals(other);

    public override int GetHashCode() => ToUInt64().GetHashCode();

    /// <summary>The decimal value.</summary>
    public override string ToString() => ToUInt64().ToString(CultureInfo.InvariantCulture);

    private ulong ToUInt64() =>
        _b0 | ((ulong)_b1 << 8) | ((ulong)_b2 << 16) | ((ulong)_b3 << 24) | ((ulong)_b4 << 32);
}
