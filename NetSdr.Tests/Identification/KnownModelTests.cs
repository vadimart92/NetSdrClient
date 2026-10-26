using NetSdr.Identification;

namespace NetSdr.Tests.Identification;

public class KnownModelTests
{
    [TestCase("SDR-IP", KnownModel.SdrIp)]
    [TestCase("NetSDR", KnownModel.NetSdr)]
    [TestCase("netsdr", KnownModel.NetSdr)]
    [TestCase("CloudIQ", KnownModel.CloudIq)]
    [TestCase("Cloud-IQ", KnownModel.CloudIq)]
    [TestCase("CloudSDR", KnownModel.CloudSdr)]
    [TestCase("SDR-14", KnownModel.Unknown)]
    [TestCase(null, KnownModel.Unknown)]
    public void FromName(string? name, KnownModel model) => Assert.That(KnownModelNames.FromName(name), Is.EqualTo(model));

    [Test]
    public void FromHundredths() => Assert.That(DeviceVersion.FromHundredths(529), Is.EqualTo(new Version(5, 29)));
}
