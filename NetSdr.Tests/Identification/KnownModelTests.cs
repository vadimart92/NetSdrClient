using NetSdr.Identification;

namespace NetSdr.Tests.Identification;

public class KnownModelTests
{
    [Theory]
    [InlineData("SDR-IP", KnownModel.SdrIp)]
    [InlineData("NetSDR", KnownModel.NetSdr)]
    [InlineData("netsdr", KnownModel.NetSdr)]
    [InlineData("CloudIQ", KnownModel.CloudIq)]
    [InlineData("Cloud-IQ", KnownModel.CloudIq)]
    [InlineData("CloudSDR", KnownModel.CloudSdr)]
    [InlineData("SDR-14", KnownModel.Unknown)]
    [InlineData(null, KnownModel.Unknown)]
    public void FromName(string? name, KnownModel model) => Assert.Equal(model, KnownModelNames.FromName(name));

    [Fact]
    public void FromHundredths() => Assert.Equal(new Version(5, 29), DeviceVersion.FromHundredths(529));
}
