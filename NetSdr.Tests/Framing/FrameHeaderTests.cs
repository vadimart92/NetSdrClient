using NetSdr.Framing;

namespace NetSdr.Tests.Framing;

public class FrameHeaderTests
{
    [Theory]
    [InlineData(0, 4, "04 00")]
    [InlineData(1, 4, "04 20")]
    [InlineData(2, 5, "05 40")]
    [InlineData(3, 3, "03 60")]
    [InlineData(4, 1028, "04 84")]
    [InlineData(5, 1028, "04 A4")]
    [InlineData(6, 2, "02 C0")]
    [InlineData(7, 8191, "FF FF")]
    public void Write_ThenTryRead_RoundTripsAllTypes(byte type, int length, string hex)
    {
        var buffer = new byte[2];
        FrameHeader.Write(buffer, length, type);
        Assert.Equal(Hex.Parse(hex), buffer);
        Assert.True(FrameHeader.TryRead(buffer, out var readLength, out var readType));
        Assert.Equal(length, readLength);
        Assert.Equal(type, readType);
    }

    [Theory]
    [InlineData("00 80", 4)]
    [InlineData("00 A0", 5)]
    [InlineData("00 C0", 6)]
    [InlineData("00 E0", 7)]
    public void TryRead_ZeroLengthDataItem_Means8194(string hex, byte type)
    {
        Assert.True(FrameHeader.TryRead(Hex.Parse(hex), out var length, out var readType));
        Assert.Equal(8194, length);
        Assert.Equal(type, readType);
    }

    [Theory]
    [InlineData("00 00")]
    [InlineData("01 00")]
    [InlineData("00 20")]
    [InlineData("00 60")]
    [InlineData("01 80")]
    public void TryRead_LengthBelowTwo_IsInvalid(string hex) =>
        Assert.False(FrameHeader.TryRead(Hex.Parse(hex), out _, out _));

    [Fact]
    public void TryRead_OneByte_ReturnsFalse() => Assert.False(FrameHeader.TryRead([0x04], out _, out _));

    [Fact]
    public void TryRead_BoundaryLengths()
    {
        Assert.True(FrameHeader.TryRead(Hex.Parse("02 00"), out var min, out _));
        Assert.Equal(2, min);
        Assert.True(FrameHeader.TryRead(Hex.Parse("FF 1F"), out var max, out var type));
        Assert.Equal(8191, max);
        Assert.Equal(0, type);
    }

    [Fact]
    public void Write_8194ForDataItem_EncodesZero()
    {
        var buffer = new byte[2];
        FrameHeader.Write(buffer, 8194, 4);
        Assert.Equal(Hex.Parse("00 80"), buffer);
    }

    [Theory]
    [InlineData(8194, 0)]
    [InlineData(1, 0)]
    [InlineData(8192, 4)]
    [InlineData(4, 8)]
    public void Write_InvalidArguments_Throw(int length, byte type) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameHeader.Write(new byte[2], length, type));
}
