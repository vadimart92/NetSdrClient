using NetSdr.Examples.Vega.Items;
using NetSdr.Framing;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Items;

public class VegaItemTests
{
    [Test]
    public void VendorUnlock_Frame() =>
        Assert.That(
            ControlFrames.Request(RequestType.Set, new VendorUnlock(0xC0DE_5EC5)),
            Is.EqualTo(Hex.Parse("08 00 00 80 C5 5E DE C0")));

    [Test]
    public void AntennaSelect_SetAndGetFrames()
    {
        Assert.That(
            ControlFrames.Request(RequestType.Set, new AntennaSelect(0, AntennaPort.B)),
            Is.EqualTo(Hex.Parse("06 00 01 80 00 01")));
        Assert.That(ControlFrames.Encode((byte)RequestType.Get, AntennaSelect.Code, [1]), Is.EqualTo(Hex.Parse("05 20 01 80 01")));
    }

    [Test]
    public void BoardTemperatureV1_NegativeUnsolicited()
    {
        var frame = ControlFrames.Reply(ReplyType.Unsolicited, BoardTemperatureV1.FromCelsius(TemperatureSensor.Adc, -12.5));
        Assert.That(frame, Is.EqualTo(Hex.Parse("07 20 02 80 01 1E FB")));
        Assert.That(ControlFrames.Decode<BoardTemperatureV1>(frame).Celsius, Is.EqualTo(-12.5));
    }

    [Test]
    public void BoardTemperatureV2_NegativeUnsolicited()
    {
        var frame = ControlFrames.Reply(ReplyType.Unsolicited, BoardTemperatureV2.FromCelsius(TemperatureSensor.Adc, -12.5));
        Assert.That(frame, Is.EqualTo(Hex.Parse("0A 20 02 80 01 2C CF FF FF 00")));
        var item = ControlFrames.Decode<BoardTemperatureV2>(frame);
        Assert.That((item.Celsius, item.Status), Is.EqualTo((-12.5, (byte)0)));
        Assert.That(BoardTemperatureV2.Code, Is.EqualTo(BoardTemperatureV1.Code));
    }

    [Test]
    public void VegaFirmwareInfo_Frame() =>
        Assert.That(ControlFrames.Reply(ReplyType.Response, new VegaFirmwareInfo(200)), Is.EqualTo(Hex.Parse("06 00 05 80 C8 00")));

    [Test]
    public void OverloadEvent_Frame() =>
        Assert.That(
            ControlFrames.Reply(ReplyType.Unsolicited, new OverloadEvent(1, OverloadedStages.Adc | OverloadedStages.Rf)),
            Is.EqualTo(Hex.Parse("06 20 04 80 01 03")));

    [Test]
    public void DeviceLabel_RoundTrip()
    {
        var frame = ControlFrames.Request(RequestType.Set, new DeviceLabel("VEGA-1"));
        Assert.That(frame, Is.EqualTo(Hex.Parse("0B 00 03 80 56 45 47 41 2D 31 00")));
        Assert.That(ControlFrames.Decode<DeviceLabel>(frame).Value, Is.EqualTo("VEGA-1"));
    }

    [Test]
    public void DeviceLabel_Limits()
    {
        Assert.That(new DeviceLabel(new string('x', 32)).Value.Length, Is.EqualTo(32));
        Assert.That(() => new DeviceLabel(new string('x', 33)), Throws.TypeOf<ArgumentException>());
        Assert.That(() => new DeviceLabel("Вега"), Throws.TypeOf<ArgumentException>());
        Assert.That(default(DeviceLabel).Value, Is.EqualTo(""));
        Assert.That(ControlFrames.Decode<DeviceLabel>(Hex.Parse("06 00 03 80 41 42")).Value, Is.EqualTo("AB"));
    }

    [Test]
    public void DeviceLabel_OverLongFromWire_IsRead_ButNeverWritten()
    {
        // Reading is lenient: a device may report a label longer than the host would ever send.
        var overLong = ControlFrames.Decode<DeviceLabel>(Hex.Parse("25 00 03 80 " + string.Concat(Enumerable.Repeat("78", 33))));
        Assert.That(overLong.Value, Is.EqualTo(new string('x', 33)));

        // Writing is not: an over-long label must not reach the wire.
        Assert.That(() => ControlFrames.Request(RequestType.Set, overLong), Throws.TypeOf<ArgumentException>());
        Assert.That(() => DeviceLabel.Write(overLong, new byte[64]), Throws.TypeOf<ArgumentException>());

        // The limit itself is still written, zero included.
        var atLimit = new DeviceLabel(new string('x', VegaProtocol.MaxLabelLength));
        var payload = new byte[DeviceLabel.GetSize(atLimit)];
        DeviceLabel.Write(atLimit, payload);
        Assert.That(payload[^1], Is.EqualTo(0));
    }
}
