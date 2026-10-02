using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Tests.Items;

public class UInt40Tests
{
    [Theory]
    [InlineData(0UL, "00 00 00 00 00")]
    [InlineData(14_010_000UL, "90 C6 D5 00 00")]
    [InlineData(7_123_456_789UL, "15 53 97 A8 01")]
    [InlineData(UInt40.MaxValue, "FF FF FF FF FF")]
    public void Layout_IsFiveBytesLittleEndian(ulong value, string hex)
    {
        UInt40 v = value;
        Assert.Equal(5, Unsafe.SizeOf<UInt40>());
        Assert.Equal(Hex.Parse(hex), MemoryMarshal.AsBytes(new ReadOnlySpan<UInt40>(in v)).ToArray());
        Assert.Equal(value, (ulong)v);
    }

    [Fact]
    public void ToString_IsDecimalValue() =>
        Assert.Equal("7123456789", ((UInt40)7_123_456_789UL).ToString());

    [Fact]
    public void Equality_FollowsValue()
    {
        UInt40 a = 14_010_000UL;
        UInt40 b = 14_010_000UL;
        UInt40 c = 14_010_001UL;
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void FromValueAbove40Bits_Throws() =>
        Assert.Throws<OverflowException>(() => { UInt40 v = UInt40.MaxValue + 1; });
}
