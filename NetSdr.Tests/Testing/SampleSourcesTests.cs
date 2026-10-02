using System.Runtime.InteropServices;
using NetSdr.Data;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class SampleSourcesTests
{
    [Fact]
    public void Counter_Int16_And_Int24()
    {
        var b16 = new byte[8];
        SampleSources.Counter()(b16, 5, SampleFormat.Int16);
        Assert.Equal(Hex.Parse("05 00 FA FF 06 00 F9 FF"), b16);
        var b24 = new byte[6];
        SampleSources.Counter()(b24, 1, SampleFormat.Int24);
        Assert.Equal(Hex.Parse("01 00 00 FE FF FF"), b24);
    }

    [Fact]
    public void Counter_KeepsLowBytesOfLargeIndex()
    {
        var b = new byte[4];
        SampleSources.Counter()(b, 0x12345, SampleFormat.Int16);
        Assert.Equal(Hex.Parse("45 23 BA DC"), b);
    }

    [Fact]
    public void FromBuffer_Loops()
    {
        var source = SampleSources.FromBuffer(Hex.Parse("01 02 03 04 05 06 07 08"));
        var b = new byte[12];
        source(b, 1, SampleFormat.Int16);
        Assert.Equal(Hex.Parse("05 06 07 08 01 02 03 04 05 06 07 08"), b);
    }

    [Fact]
    public void FromBuffer_PositionFollowsSampleSize()
    {
        var source = SampleSources.FromBuffer(Hex.Parse("01 02 03 04 05 06 07 08 09 0A 0B 0C"));
        var b = new byte[6];
        source(b, 3, SampleFormat.Int24);   // 3 samples of 6 bytes = byte 18, 18 mod 12 = 6
        Assert.Equal(Hex.Parse("07 08 09 0A 0B 0C"), b);
    }

    [Fact]
    public void Tone_QuarterPeriod()
    {
        var b = new byte[12];
        SampleSources.Tone(1000, 8000, 0.5)(b, 0, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(b);
        Assert.Equal(16384, s[0]);
        Assert.Equal(0, s[1]);
        Assert.InRange(s[4], -1, 1);
        Assert.Equal(16384, s[5]);
    }

    [Fact]
    public void Tone_HalfPeriodIsNegativeFullScale()
    {
        var b = new byte[4];
        SampleSources.Tone(1000, 8000, 1.0)(b, 4, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(b);
        Assert.Equal(-32767, s[0]);
        Assert.InRange(s[1], -1, 1);
    }

    [Fact]
    public void Tone_Int24_UsesFullScaleOf24Bits()
    {
        var b = new byte[6];
        SampleSources.Tone(1000, 8000, 1.0)(b, 0, SampleFormat.Int24);
        Assert.Equal(Hex.Parse("FF FF 7F 00 00 00"), b);
    }

    [Fact]
    public void Tone_StaysAccurateAtLargeIndex()
    {
        // 1 kHz at 8 kHz: the phase repeats every 8 samples, also far from the start.
        var b = new byte[8];
        long far = 8 * 1_000_000_000L;
        SampleSources.Tone(1000, 8000, 1.0)(b, far + 2, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(b);
        Assert.InRange(s[0], -1, 1);
        Assert.Equal(32767, s[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Sources_ZeroTheBytesThatDoNotMakeAWholeSample(int extra)
    {
        foreach (var source in new[]
                 {
                     SampleSources.Counter(),
                     SampleSources.FromBuffer(Hex.Parse("01 02 03 04")),
                     SampleSources.Tone(1000, 8000, 1.0),
                 })
        {
            var b = new byte[4 + extra];
            Array.Fill(b, (byte)0xEE);
            source(b, 0, SampleFormat.Int16);
            Assert.All(b[4..], x => Assert.Equal(0, x));
        }
    }

    [Fact]
    public void Sources_RejectUnknownFormat()
    {
        foreach (var source in new[]
                 {
                     SampleSources.Counter(),
                     SampleSources.FromBuffer(Hex.Parse("01 02 03 04")),
                     SampleSources.Tone(1000, 8000, 1.0),
                 })
        {
            Assert.ThrowsAny<ArgumentException>(() => source(new byte[8], 0, SampleFormat.Unknown));
        }
    }

    [Fact]
    public void FromBuffer_RejectsEmptyBuffer() =>
        Assert.Throws<ArgumentException>(() => SampleSources.FromBuffer(ReadOnlyMemory<byte>.Empty));

    [Theory]
    [InlineData(1000, 0, 0.5)]
    [InlineData(1000, -8000, 0.5)]
    [InlineData(1000, double.NaN, 0.5)]
    [InlineData(1000, 8000, -0.1)]
    [InlineData(1000, 8000, 1.1)]
    [InlineData(1000, 8000, double.NaN)]
    [InlineData(double.PositiveInfinity, 8000, 0.5)]
    public void Tone_RejectsParametersOutOfRange(double frequency, double sampleRate, double amplitude) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleSources.Tone(frequency, sampleRate, amplitude));
}
