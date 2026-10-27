using NetSdr.Data;

namespace NetSdr.Tests.Data;

public class PacketTimestampTests
{
    private static readonly SampleRateTimeScale Megahertz = new(1_000_000);

    [Test]
    public void Write_TicksThenSeconds_LittleEndian()
    {
        var bytes = new byte[PacketTimestamp.Size];
        new PacketTimestamp(0x04030201, 0x08070605).Write(bytes);
        Assert.That(bytes, Is.EqualTo(Hex.Parse("01 02 03 04 05 06 07 08")));
        Assert.That(PacketTimestamp.Read(bytes), Is.EqualTo(new PacketTimestamp(0x04030201, 0x08070605)));
    }

    [Test]
    public void ReadTrailer_ReadsTheLastEightBytes()
    {
        var samples = Hex.Parse("AA BB CC DD 01 00 00 00 02 00 00 00");
        Assert.That(PacketTimestamp.ReadTrailer(samples), Is.EqualTo(new PacketTimestamp(1, 2)));
        Assert.That(() => PacketTimestamp.ReadTrailer(new byte[7]), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ToDateTimeOffset_AddsTicksAtTheSampleRate()
    {
        var time = new PacketTimestamp(250_000, 1_800_000_000).ToDateTimeOffset(Megahertz);
        Assert.That(time, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).AddMilliseconds(250)));
    }

    [Test]
    public void ToDateTimeOffset_RoundsToTheNearest100Nanoseconds()
    {
        // At 3 MHz one tick is 333.3 ns, two are 666.7 ns.
        var second = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var scale = new SampleRateTimeScale(3_000_000);
        Assert.That(new PacketTimestamp(1, 1_800_000_000).ToDateTimeOffset(scale), Is.EqualTo(second.AddTicks(3)));
        Assert.That(new PacketTimestamp(2, 1_800_000_000).ToDateTimeOffset(scale), Is.EqualTo(second.AddTicks(7)));
    }

    [TestCase(0, 0u, 1_800_000_000u)]
    [TestCase(999_999, 999_999u, 1_800_000_000u)]
    [TestCase(1_000_000, 0u, 1_800_000_001u)]
    [TestCase(86_400_000_123L, 123u, 1_800_086_400u)]
    public void AtSample_FromAWholeSecond(long sampleIndex, uint ticks, uint seconds)
    {
        var origin = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.That(PacketTimestamp.AtSample(origin, sampleIndex, Megahertz), Is.EqualTo(new PacketTimestamp(ticks, seconds)));
    }

    [Test]
    public void AtSample_OriginWithinASecond_CarriesIntoTheNext()
    {
        var origin = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).AddMilliseconds(750);
        Assert.That(PacketTimestamp.AtSample(origin, 0, Megahertz), Is.EqualTo(new PacketTimestamp(750_000, 1_800_000_000)));
        Assert.That(PacketTimestamp.AtSample(origin, 300_000, Megahertz), Is.EqualTo(new PacketTimestamp(50_000, 1_800_000_001)));
    }

    [Test]
    public void AtSample_BetweenTwoTicks_TakesTheEarlier()
    {
        // 0.5 us is half a tick at 1 MHz.
        var origin = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).AddTicks(5);
        Assert.That(PacketTimestamp.AtSample(origin, 1, Megahertz), Is.EqualTo(new PacketTimestamp(1, 1_800_000_000)));
    }

    [Test]
    public void AtSample_RoundTripsThroughToDateTimeOffset()
    {
        var origin = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).AddMilliseconds(123);
        var stamp = PacketTimestamp.AtSample(origin, 1_000, Megahertz);
        Assert.That(stamp.ToDateTimeOffset(Megahertz), Is.EqualTo(origin.AddMilliseconds(1)));
    }

    [Test]
    public void AtSample_RejectsNegativeIndexAndTimesOutsideTheRange()
    {
        var origin = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.That(() => PacketTimestamp.AtSample(origin, -1, Megahertz), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => PacketTimestamp.AtSample(DateTimeOffset.UnixEpoch.AddSeconds(-1), 0, Megahertz),
            Throws.TypeOf<OverflowException>());
    }

    [Test]
    public void SampleRateTimeScale_TicksPer100Nanoseconds()
    {
        Assert.That(new SampleRateTimeScale(10_000_000).TicksPer100Nanoseconds, Is.EqualTo(1));
        Assert.That(() => new SampleRateTimeScale(0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new SampleRateTimeScale(double.PositiveInfinity), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
