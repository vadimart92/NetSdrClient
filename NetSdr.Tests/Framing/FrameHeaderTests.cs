using NetSdr.Framing;

namespace NetSdr.Tests.Framing;

public class FrameHeaderTests
{
    [TestCase(0, 4, "04 00")]
    [TestCase(1, 4, "04 20")]
    [TestCase(2, 5, "05 40")]
    [TestCase(3, 3, "03 60")]
    [TestCase(4, 1028, "04 84")]
    [TestCase(5, 1028, "04 A4")]
    [TestCase(6, 2, "02 C0")]
    [TestCase(7, 8191, "FF FF")]
    public void Write_ThenTryRead_RoundTripsAllTypes(byte type, int length, string hex)
    {
        var buffer = new byte[2];
        FrameHeader.Write(buffer, length, type);
        Assert.That(buffer, Is.EqualTo(Hex.Parse(hex)));
        Assert.That(FrameHeader.TryRead(buffer, out var readLength, out var readType), Is.True);
        Assert.That(readLength, Is.EqualTo(length));
        Assert.That(readType, Is.EqualTo(type));
    }

    [TestCase("00 80", 4)]
    [TestCase("00 A0", 5)]
    [TestCase("00 C0", 6)]
    [TestCase("00 E0", 7)]
    public void TryRead_ZeroLengthDataItem_Means8194(string hex, byte type)
    {
        Assert.That(FrameHeader.TryRead(Hex.Parse(hex), out var length, out var readType), Is.True);
        Assert.That(length, Is.EqualTo(8194));
        Assert.That(readType, Is.EqualTo(type));
    }

    [TestCase("00 00")]
    [TestCase("01 00")]
    [TestCase("00 20")]
    [TestCase("00 60")]
    [TestCase("01 80")]
    public void TryRead_LengthBelowTwo_IsInvalid(string hex) =>
        Assert.That(FrameHeader.TryRead(Hex.Parse(hex), out _, out _), Is.False);

    [Test]
    public void TryRead_OneByte_ReturnsFalse() => Assert.That(FrameHeader.TryRead([0x04], out _, out _), Is.False);

    [Test]
    public void TryRead_BoundaryLengths()
    {
        Assert.That(FrameHeader.TryRead(Hex.Parse("02 00"), out var min, out _), Is.True);
        Assert.That(min, Is.EqualTo(2));
        Assert.That(FrameHeader.TryRead(Hex.Parse("FF 1F"), out var max, out var type), Is.True);
        Assert.That(max, Is.EqualTo(8191));
        Assert.That(type, Is.EqualTo(0));
    }

    [Test]
    public void Write_8194ForDataItem_EncodesZero()
    {
        var buffer = new byte[2];
        FrameHeader.Write(buffer, 8194, 4);
        Assert.That(buffer, Is.EqualTo(Hex.Parse("00 80")));
    }

    [TestCase(8194, 0)]
    [TestCase(1, 0)]
    [TestCase(8192, 4)]
    [TestCase(4, 8)]
    public void Write_InvalidArguments_Throw(int length, byte type) =>
        Assert.That(() => FrameHeader.Write(new byte[2], length, type), Throws.TypeOf<ArgumentOutOfRangeException>());
}
