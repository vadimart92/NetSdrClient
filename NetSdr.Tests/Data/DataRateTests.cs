using NetSdr.Data;

namespace NetSdr.Tests.Data;

public class DataRateTests
{
    [Theory]
    [InlineData(500_000, SampleFormat.Int16, 1, 2_000_000)]
    [InlineData(1_333_333, SampleFormat.Int24, 1, 7_999_998)]
    [InlineData(200_000, SampleFormat.Int16, 2, 1_600_000)]
    public void BytesPerSecond(double rate, SampleFormat format, int channels, long expected) =>
        Assert.Equal(expected, DataRate.BytesPerSecond(rate, format, channels));

    [Fact]
    public void Unknown_Throws() =>
        Assert.Throws<ArgumentException>(() => DataRate.BytesPerSecond(1, SampleFormat.Unknown));
}
