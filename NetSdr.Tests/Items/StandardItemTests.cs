using System.Net;
using System.Runtime.CompilerServices;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Items;

public class StandardItemTests
{
    private static void AssertSetFrame<T>(T item, string hex) where T : struct, IControlItem<T>
    {
        var frame = ControlFrames.Request(RequestType.Set, item);
        Assert.That(frame, Is.EqualTo(Hex.Parse(hex)));
        Assert.That(ControlFrames.Decode<T>(frame), Is.EqualTo(item));
    }

    [Test]
    public void ReceiverFrequency_14_010MHz() =>
        AssertSetFrame(new ReceiverFrequency(ReceiverFrequency.Channel1, 14_010_000), "0A 00 20 00 00 90 C6 D5 00 00");
    [Test]
    public void ReceiverFrequency_Display_7_123456789GHz() =>
        AssertSetFrame(new ReceiverFrequency(ReceiverFrequency.Display, 7_123_456_789), "0A 00 20 00 01 15 53 97 A8 01");
    [Test] public void RfGain_Minus20() => AssertSetFrame(new RfGain(0, -20), "06 00 38 00 00 EC");
    [Test] public void AfGain_10() => AssertSetFrame(new AfGain(0, 10), "06 00 48 00 00 0A");
    [Test] public void RfFilter_5_5To7() => AssertSetFrame(new RfFilter(0, RfFilterSelection.Band5_5To7), "06 00 44 00 00 05");
    [Test]
    public void AdModes_DitherAndGain() =>
        AssertSetFrame(new AdModes(0, AdModes.Dither | AdModes.Gain1_5), "06 00 8A 00 00 03");
    [Test]
    public void InputSyncMode_NegativeEdge1000() =>
        AssertSetFrame(new InputSyncMode(0, InputSyncMode.NegativeEdgeStart, 1000), "08 00 B4 00 00 01 E8 03");
    [Test] public void OutputSampleRate_500k() => AssertSetFrame(new OutputSampleRate(0, 500_000), "09 00 B8 00 00 20 A1 07 00");
    [Test]
    public void AdInputSampleRate_80_000_123() =>
        AssertSetFrame(new AdInputSampleRate(0, 80_000_123), "09 00 B0 00 00 7B B4 C4 04");
    [Test] public void DcCalibration_Minus234() => AssertSetFrame(new DcCalibration(0, -234), "07 00 D0 00 00 16 FF");
    [Test]
    public void PulseOutputMode_SampleRate() =>
        AssertSetFrame(new PulseOutputMode(0, PulseOutputMode.SampleRate), "06 00 B6 00 00 03");
    [Test] public void DacOutputMode_NcoTrack() => AssertSetFrame(new DacOutputMode(0, DacOutputMode.NcoTrack), "06 00 2A 01 00 02");
    [Test]
    public void DataOutputPacketSize_Small() =>
        AssertSetFrame(new DataOutputPacketSize(DataOutputPacketSize.Small), "05 00 C4 00 01");
    [Test]
    public void ReceiverChannelSetup_Dual() =>
        AssertSetFrame(new ReceiverChannelSetup(ReceiverChannelSetup.DualChannelSingleAd), "05 00 19 00 04");
    [Test]
    public void ReceiverState_Start24BitComplex() =>
        AssertSetFrame(ReceiverState.Start(complex: true, bits24: true), "08 00 18 00 80 02 80 00");
    [Test] public void ReceiverState_Stop() => AssertSetFrame(ReceiverState.Stop, "08 00 18 00 00 01 00 00");
    [Test]
    public void ReceiverState_FifoAndTriggered()
    {
        AssertSetFrame(ReceiverState.Start(false, false, CaptureMode.Fifo, 4), "08 00 18 00 00 02 01 04");
        var triggered = ReceiverState.Start(true, true, CaptureMode.HardwareTriggered);
        Assert.That(triggered.CaptureMode, Is.EqualTo(0x83));
        Assert.That(triggered.IsRunning && triggered.IsComplex && triggered.Is24Bit, Is.True);
    }

    [Test]
    public void DataOutputUdpAddress_FromEndPoint()
    {
        var endPoint = new IPEndPoint(IPAddress.Parse("192.168.3.123"), 12345);
        var item = DataOutputUdpAddress.For(endPoint);
        AssertSetFrame(item, "0A 00 C5 00 7B 03 A8 C0 39 30");
        Assert.That(item.ToEndPoint(), Is.EqualTo(endPoint));
        Assert.That(() => DataOutputUdpAddress.For(new IPEndPoint(IPAddress.IPv6Loopback, 1)), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void InterfaceVersion_529() =>
        Assert.That(ControlFrames.Decode<InterfaceVersion>(Hex.Parse("06 00 03 00 11 02")).Version, Is.EqualTo(529));

    [Test]
    public void FirmwareVersion_AppAndFpga()
    {
        var app = ControlFrames.Decode<FirmwareVersion>(Hex.Parse("07 00 04 00 01 11 02"));
        Assert.That((app.Id, app.Version), Is.EqualTo((1, 529)));
        var fpga = ControlFrames.Decode<FirmwareVersion>(Hex.Parse("07 00 04 00 03 03 1C"));
        Assert.That((fpga.FpgaConfigId, fpga.FpgaRevision), Is.EqualTo((3, 28)));
    }

    [Test]
    public void ProductId_SdrIp() =>
        Assert.That(ControlFrames.Decode<ProductId>(Hex.Parse("08 00 09 00 53 44 52 03")).Value, Is.EqualTo(0x03524453u));

    [Test]
    public void Options_Reflock()
    {
        var options = ControlFrames.Decode<Options>(Hex.Parse("0A 00 0A 00 02 00 00 00 00 00"));
        Assert.That((options.Flags, options.Custom, options.Detail), Is.EqualTo((Options.ReflockBoard, 0, 0u)));
    }

    [Test]
    public void SecurityCode_Value() =>
        Assert.That(ControlFrames.Decode<SecurityCode>(Hex.Parse("08 00 0B 00 EF BE AD DE")).Value, Is.EqualTo(0xDEADBEEFu));

    [Test]
    public void ReadOnlyItems_HaveWireSize()
    {
        // The decode-only tests above ignore trailing bytes, so pin the sizes of these items explicitly.
        Assert.That(Unsafe.SizeOf<InterfaceVersion>(), Is.EqualTo(2));
        Assert.That(Unsafe.SizeOf<FirmwareVersion>(), Is.EqualTo(3));
        Assert.That(Unsafe.SizeOf<ProductId>(), Is.EqualTo(4));
        Assert.That(Unsafe.SizeOf<Options>(), Is.EqualTo(6));
        Assert.That(Unsafe.SizeOf<SecurityCode>(), Is.EqualTo(4));
    }
}
