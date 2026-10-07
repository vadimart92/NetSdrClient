using NetSdr.Examples.Vega.Items;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaCommandTests
{
    [Fact]
    public async Task Antenna_PerChannel()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        await vega.SelectAntennaAsync(0, AntennaPort.B);
        await vega.SelectAntennaAsync(1, AntennaPort.Loop);
        Assert.Equal(AntennaPort.B, await vega.GetAntennaAsync(0));
        Assert.Equal(AntennaPort.Loop, await vega.GetAntennaAsync(1));
        Assert.Equal(AntennaPort.A, await vega.GetAntennaAsync(2));
    }

    [Theory]
    [InlineData(VegaFirmware.V1)]
    [InlineData(VegaFirmware.V2)]
    public async Task Temperature_ReadsFormatOfFirmware(VegaFirmware firmware)
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync(firmware);
        await using var _ = emulator;
        await using var __ = vega;
        emulator.SetTemperature(TemperatureSensor.Adc, 41.25);
        emulator.SetTemperature(TemperatureSensor.Board, -12.5);
        Assert.Equal(41.25, await vega.GetTemperatureAsync(TemperatureSensor.Adc));
        Assert.Equal(-12.5, await vega.GetTemperatureAsync(TemperatureSensor.Board));
        await Assert.ThrowsAsync<NetSdrNakException>(() => vega.GetTemperatureAsync(TemperatureSensor.Fpga));
    }

    [Fact]
    public async Task Label_RoundTrip()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        await vega.SetLabelAsync("Roof antenna");
        Assert.Equal("Roof antenna", await vega.GetLabelAsync());
    }

    [Fact]
    public async Task Label_TooLong_ThrowsBeforeSending()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        await Assert.ThrowsAsync<ArgumentException>(() => vega.SetLabelAsync(new string('x', 33)));
        Assert.DoesNotContain(emulator.Server.Received, r => r.Code == VegaProtocol.DeviceLabelCode);
    }
}
