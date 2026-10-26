using System.Runtime.InteropServices;
using NetSdr.Data;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class SampleSourcesTests
{
    [Test]
    public void Counter_Int16_And_Int24()
    {
        var b16 = new byte[8];
        SampleSources.Counter()(b16, 5, SampleFormat.Int16);
        Assert.That(b16, Is.EqualTo(Hex.Parse("05 00 FA FF 06 00 F9 FF")));
        var b24 = new byte[6];
        SampleSources.Counter()(b24, 1, SampleFormat.Int24);
        Assert.That(b24, Is.EqualTo(Hex.Parse("01 00 00 FE FF FF")));
    }

    [Test]
    public void Counter_KeepsLowBytesOfLargeIndex()
    {
        var b = new byte[4];
        SampleSources.Counter()(b, 0x12345, SampleFormat.Int16);
        Assert.That(b, Is.EqualTo(Hex.Parse("45 23 BA DC")));
    }

    [Test]
    public void FromBuffer_Loops()
    {
        var source = SampleSources.FromBuffer(Hex.Parse("01 02 03 04 05 06 07 08"));
        var b = new byte[12];
        source(b, 1, SampleFormat.Int16);
        Assert.That(b, Is.EqualTo(Hex.Parse("05 06 07 08 01 02 03 04 05 06 07 08")));
    }

    [Test]
    public void FromBuffer_PositionFollowsSampleSize()
    {
        var source = SampleSources.FromBuffer(Hex.Parse("01 02 03 04 05 06 07 08 09 0A 0B 0C"));
        var b = new byte[6];
        source(b, 3, SampleFormat.Int24);   // 3 samples of 6 bytes = byte 18, 18 mod 12 = 6
        Assert.That(b, Is.EqualTo(Hex.Parse("07 08 09 0A 0B 0C")));
    }

    [Test]
    public void Tone_QuarterPeriod()
    {
        var b = new byte[12];
        SampleSources.Tone(1000, 8000, 0.5)(b, 0, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(b);
        Assert.That(s[0], Is.EqualTo(16384));
        Assert.That(s[1], Is.EqualTo(0));
        Assert.That(s[4], Is.InRange(-1, 1));
        Assert.That(s[5], Is.EqualTo(16384));
    }

    [Test]
    public void Tone_HalfPeriodIsNegativeFullScale()
    {
        var b = new byte[4];
        SampleSources.Tone(1000, 8000, 1.0)(b, 4, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(b);
        Assert.That(s[0], Is.EqualTo(-32767));
        Assert.That(s[1], Is.InRange(-1, 1));
    }

    [Test]
    public void Tone_Int24_UsesFullScaleOf24Bits()
    {
        var b = new byte[6];
        SampleSources.Tone(1000, 8000, 1.0)(b, 0, SampleFormat.Int24);
        Assert.That(b, Is.EqualTo(Hex.Parse("FF FF 7F 00 00 00")));
    }

    [Test]
    public void Tone_StaysAccurateAtLargeIndex()
    {
        // 1 kHz at 8 kHz: the phase repeats every 8 samples, also far from the start.
        var b = new byte[8];
        long far = 8 * 1_000_000_000L;
        SampleSources.Tone(1000, 8000, 1.0)(b, far + 2, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(b);
        Assert.That(s[0], Is.InRange(-1, 1));
        Assert.That(s[1], Is.EqualTo(32767));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
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
            Assert.That(b[4..], Has.All.EqualTo(0));
        }
    }

    [Test]
    public void Sources_RejectUnknownFormat()
    {
        foreach (var source in new[]
                 {
                     SampleSources.Counter(),
                     SampleSources.FromBuffer(Hex.Parse("01 02 03 04")),
                     SampleSources.Tone(1000, 8000, 1.0),
                 })
        {
            Assert.That(() => source(new byte[8], 0, SampleFormat.Unknown), Throws.InstanceOf<ArgumentException>());
        }
    }

    [Test]
    public void FromBuffer_RejectsEmptyBuffer() =>
        Assert.That(() => SampleSources.FromBuffer(ReadOnlyMemory<byte>.Empty), Throws.TypeOf<ArgumentException>());

    [TestCase(1000, 0, 0.5)]
    [TestCase(1000, -8000, 0.5)]
    [TestCase(1000, double.NaN, 0.5)]
    [TestCase(1000, 8000, -0.1)]
    [TestCase(1000, 8000, 1.1)]
    [TestCase(1000, 8000, double.NaN)]
    [TestCase(double.PositiveInfinity, 8000, 0.5)]
    public void Tone_RejectsParametersOutOfRange(double frequency, double sampleRate, double amplitude) =>
        Assert.That(() => SampleSources.Tone(frequency, sampleRate, amplitude), Throws.TypeOf<ArgumentOutOfRangeException>());
}
