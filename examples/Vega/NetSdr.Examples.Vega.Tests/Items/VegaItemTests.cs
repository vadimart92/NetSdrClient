using NetSdr.Examples.Vega.Items;
using NetSdr.Framing;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Items;

public class VegaItemTests
{
    [Fact] public void VendorUnlock_Frame() =>
        Assert.Equal(Hex.Parse("08 00 00 80 C5 5E DE C0"), ControlFrames.Request(RequestType.Set, new VendorUnlock(0xC0DE_5EC5)));

    [Fact]
    public void AntennaSelect_SetAndGetFrames()
    {
        Assert.Equal(Hex.Parse("06 00 01 80 00 01"), ControlFrames.Request(RequestType.Set, new AntennaSelect(0, AntennaPort.B)));
        Assert.Equal(Hex.Parse("05 20 01 80 01"), ControlFrames.Encode((byte)RequestType.Get, AntennaSelect.Code, [1]));
    }

    [Fact]
    public void BoardTemperatureV1_NegativeUnsolicited()
    {
        var frame = ControlFrames.Reply(ReplyType.Unsolicited, BoardTemperatureV1.FromCelsius(TemperatureSensor.Adc, -12.5));
        Assert.Equal(Hex.Parse("07 20 02 80 01 1E FB"), frame);
        Assert.Equal(-12.5, ControlFrames.Decode<BoardTemperatureV1>(frame).Celsius);
    }

    [Fact]
    public void BoardTemperatureV2_NegativeUnsolicited()
    {
        var frame = ControlFrames.Reply(ReplyType.Unsolicited, BoardTemperatureV2.FromCelsius(TemperatureSensor.Adc, -12.5));
        Assert.Equal(Hex.Parse("0A 20 02 80 01 2C CF FF FF 00"), frame);
        var item = ControlFrames.Decode<BoardTemperatureV2>(frame);
        Assert.Equal((-12.5, (byte)0), (item.Celsius, item.Status));
        Assert.Equal(BoardTemperatureV1.Code, BoardTemperatureV2.Code);
    }

    [Fact] public void VegaFirmwareInfo_Frame() =>
        Assert.Equal(Hex.Parse("06 00 05 80 C8 00"), ControlFrames.Reply(ReplyType.Response, new VegaFirmwareInfo(200)));

    [Fact] public void OverloadEvent_Frame() =>
        Assert.Equal(Hex.Parse("06 20 04 80 01 03"),
            ControlFrames.Reply(ReplyType.Unsolicited, new OverloadEvent(1, OverloadFlags.Adc | OverloadFlags.Rf)));

    [Fact]
    public void DeviceLabel_RoundTrip()
    {
        var frame = ControlFrames.Request(RequestType.Set, new DeviceLabel("VEGA-1"));
        Assert.Equal(Hex.Parse("0B 00 03 80 56 45 47 41 2D 31 00"), frame);
        Assert.Equal("VEGA-1", ControlFrames.Decode<DeviceLabel>(frame).Value);
    }

    [Fact]
    public void DeviceLabel_Limits()
    {
        Assert.Equal(32, new DeviceLabel(new string('x', 32)).Value.Length);
        Assert.Throws<ArgumentException>(() => new DeviceLabel(new string('x', 33)));
        Assert.Throws<ArgumentException>(() => new DeviceLabel("Вега"));
        Assert.Equal("", default(DeviceLabel).Value);
        Assert.Equal("AB", ControlFrames.Decode<DeviceLabel>(Hex.Parse("06 00 03 80 41 42")).Value);
    }
}
