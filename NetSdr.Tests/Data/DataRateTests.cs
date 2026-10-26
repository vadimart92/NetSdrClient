using NetSdr.Data;

namespace NetSdr.Tests.Data;

public class DataRateTests
{
    [TestCase(500_000, SampleFormat.Int16, 1, 2_000_000)]
    [TestCase(1_333_333, SampleFormat.Int24, 1, 7_999_998)]
    [TestCase(200_000, SampleFormat.Int16, 2, 1_600_000)]
    public void BytesPerSecond(double rate, SampleFormat format, int channels, long expected) =>
        Assert.That(DataRate.BytesPerSecond(rate, format, channels), Is.EqualTo(expected));

    [Test]
    public void Unknown_Throws() =>
        Assert.That(() => DataRate.BytesPerSecond(1, SampleFormat.Unknown), Throws.TypeOf<ArgumentException>());
}
