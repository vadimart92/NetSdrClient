using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Tests.Items;

public class UInt40Tests
{
    [TestCase(0UL, "00 00 00 00 00")]
    [TestCase(14_010_000UL, "90 C6 D5 00 00")]
    [TestCase(7_123_456_789UL, "15 53 97 A8 01")]
    [TestCase(UInt40.MaxValue, "FF FF FF FF FF")]
    public void Layout_IsFiveBytesLittleEndian(ulong value, string hex)
    {
        UInt40 v = value;
        Assert.That(Unsafe.SizeOf<UInt40>(), Is.EqualTo(5));
        Assert.That(MemoryMarshal.AsBytes(new ReadOnlySpan<UInt40>(in v)).ToArray(), Is.EqualTo(Hex.Parse(hex)));
        Assert.That((ulong)v, Is.EqualTo(value));
    }

    [Test]
    public void ToString_IsDecimalValue() =>
        Assert.That(((UInt40)7_123_456_789UL).ToString(), Is.EqualTo("7123456789"));

    [Test]
    public void Equality_FollowsValue()
    {
        UInt40 a = 14_010_000UL;
        UInt40 b = 14_010_000UL;
        UInt40 c = 14_010_001UL;
        Assert.That(b, Is.EqualTo(a));
        Assert.That(b.GetHashCode(), Is.EqualTo(a.GetHashCode()));
        Assert.That(c, Is.Not.EqualTo(a));
    }

    [Test]
    public void FromValueAbove40Bits_Throws() =>
        Assert.That(() => { UInt40 v = UInt40.MaxValue + 1; }, Throws.TypeOf<OverflowException>());
}
