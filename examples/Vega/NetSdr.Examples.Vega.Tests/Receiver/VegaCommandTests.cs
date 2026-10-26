using NetSdr.Examples.Vega.Items;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaCommandTests
{
    [Test]
    public async Task Antenna_PerChannel()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        await vega.SelectAntennaAsync(0, AntennaPort.B);
        await vega.SelectAntennaAsync(1, AntennaPort.Loop);
        Assert.That(await vega.GetAntennaAsync(0), Is.EqualTo(AntennaPort.B));
        Assert.That(await vega.GetAntennaAsync(1), Is.EqualTo(AntennaPort.Loop));
        Assert.That(await vega.GetAntennaAsync(2), Is.EqualTo(AntennaPort.A));
    }

    [TestCase(VegaFirmware.V1)]
    [TestCase(VegaFirmware.V2)]
    public async Task Temperature_ReadsFormatOfFirmware(VegaFirmware firmware)
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync(firmware);
        await using var _ = emulator;
        await using var __ = vega;
        emulator.SetTemperature(TemperatureSensor.Adc, 41.25);
        emulator.SetTemperature(TemperatureSensor.Board, -12.5);
        Assert.That(await vega.GetTemperatureAsync(TemperatureSensor.Adc), Is.EqualTo(41.25));
        Assert.That(await vega.GetTemperatureAsync(TemperatureSensor.Board), Is.EqualTo(-12.5));
        await Assert.ThatAsync(() => vega.GetTemperatureAsync(TemperatureSensor.Fpga), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task Label_RoundTrip()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        await vega.SetLabelAsync("Roof antenna");
        Assert.That(await vega.GetLabelAsync(), Is.EqualTo("Roof antenna"));
    }

    [Test]
    public async Task Label_TooLong_ThrowsBeforeSending()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        await Assert.ThatAsync(() => vega.SetLabelAsync(new string('x', 33)), Throws.TypeOf<ArgumentException>());
        Assert.That(emulator.Server.Received.Select(r => r.Code), Does.Not.Contain(VegaProtocol.DeviceLabelCode));
    }
}
