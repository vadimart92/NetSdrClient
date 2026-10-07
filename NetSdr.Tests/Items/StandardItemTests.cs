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
        Assert.Equal(Hex.Parse(hex), frame);
        Assert.Equal(item, ControlFrames.Decode<T>(frame));
    }

    [Fact]
    public void ReceiverFrequency_14_010MHz() =>
        AssertSetFrame(new ReceiverFrequency(ReceiverFrequency.Channel1, 14_010_000), "0A 00 20 00 00 90 C6 D5 00 00");
    [Fact]
    public void ReceiverFrequency_Display_7_123456789GHz() =>
        AssertSetFrame(new ReceiverFrequency(ReceiverFrequency.Display, 7_123_456_789), "0A 00 20 00 01 15 53 97 A8 01");
    [Fact] public void RfGain_Minus20() => AssertSetFrame(new RfGain(0, -20), "06 00 38 00 00 EC");
    [Fact] public void AfGain_10() => AssertSetFrame(new AfGain(0, 10), "06 00 48 00 00 0A");
    [Fact] public void RfFilter_5_5To7() => AssertSetFrame(new RfFilter(0, RfFilterSelection.Band5_5To7), "06 00 44 00 00 05");
    [Fact]
    public void AdModes_DitherAndGain() =>
        AssertSetFrame(new AdModes(0, AdModes.Dither | AdModes.Gain1_5), "06 00 8A 00 00 03");
    [Fact]
    public void InputSyncMode_NegativeEdge1000() =>
        AssertSetFrame(new InputSyncMode(0, InputSyncMode.NegativeEdgeStart, 1000), "08 00 B4 00 00 01 E8 03");
    [Fact] public void OutputSampleRate_500k() => AssertSetFrame(new OutputSampleRate(0, 500_000), "09 00 B8 00 00 20 A1 07 00");
    [Fact]
    public void AdInputSampleRate_80_000_123() =>
        AssertSetFrame(new AdInputSampleRate(0, 80_000_123), "09 00 B0 00 00 7B B4 C4 04");
    [Fact] public void DcCalibration_Minus234() => AssertSetFrame(new DcCalibration(0, -234), "07 00 D0 00 00 16 FF");
    [Fact]
    public void PulseOutputMode_SampleRate() =>
        AssertSetFrame(new PulseOutputMode(0, PulseOutputMode.SampleRate), "06 00 B6 00 00 03");
    [Fact] public void DacOutputMode_NcoTrack() => AssertSetFrame(new DacOutputMode(0, DacOutputMode.NcoTrack), "06 00 2A 01 00 02");
    [Fact]
    public void DataOutputPacketSize_Small() =>
        AssertSetFrame(new DataOutputPacketSize(DataOutputPacketSize.Small), "05 00 C4 00 01");
    [Fact]
    public void ReceiverChannelSetup_Dual() =>
        AssertSetFrame(new ReceiverChannelSetup(ReceiverChannelSetup.DualChannelSingleAd), "05 00 19 00 04");
    [Fact]
    public void ReceiverState_Start24BitComplex() =>
        AssertSetFrame(ReceiverState.Start(complex: true, bits24: true), "08 00 18 00 80 02 80 00");
    [Fact] public void ReceiverState_Stop() => AssertSetFrame(ReceiverState.Stop, "08 00 18 00 00 01 00 00");
    [Fact]
    public void ReceiverState_FifoAndTriggered()
    {
        AssertSetFrame(ReceiverState.Start(false, false, CaptureMode.Fifo, 4), "08 00 18 00 00 02 01 04");
        var triggered = ReceiverState.Start(true, true, CaptureMode.HardwareTriggered);
        Assert.Equal(0x83, triggered.CaptureMode);
        Assert.True(triggered.IsRunning && triggered.IsComplex && triggered.Is24Bit);
    }

    [Fact]
    public void DataOutputUdpAddress_FromEndPoint()
    {
        var endPoint = new IPEndPoint(IPAddress.Parse("192.168.3.123"), 12345);
        var item = DataOutputUdpAddress.For(endPoint);
        AssertSetFrame(item, "0A 00 C5 00 7B 03 A8 C0 39 30");
        Assert.Equal(endPoint, item.ToEndPoint());
        Assert.Throws<ArgumentException>(() => DataOutputUdpAddress.For(new IPEndPoint(IPAddress.IPv6Loopback, 1)));
    }

    [Fact]
    public void InterfaceVersion_529() =>
        Assert.Equal(529, ControlFrames.Decode<InterfaceVersion>(Hex.Parse("06 00 03 00 11 02")).Version);

    [Fact]
    public void FirmwareVersion_AppAndFpga()
    {
        var app = ControlFrames.Decode<FirmwareVersion>(Hex.Parse("07 00 04 00 01 11 02"));
        Assert.Equal((1, 529), (app.Id, app.Version));
        var fpga = ControlFrames.Decode<FirmwareVersion>(Hex.Parse("07 00 04 00 03 03 1C"));
        Assert.Equal((3, 28), (fpga.FpgaConfigId, fpga.FpgaRevision));
    }

    [Fact]
    public void ProductId_SdrIp() =>
        Assert.Equal(0x03524453u, ControlFrames.Decode<ProductId>(Hex.Parse("08 00 09 00 53 44 52 03")).Value);

    [Fact]
    public void Options_Reflock()
    {
        var options = ControlFrames.Decode<Options>(Hex.Parse("0A 00 0A 00 02 00 00 00 00 00"));
        Assert.Equal((Options.ReflockBoard, 0, 0u), (options.Flags, options.Custom, options.Detail));
    }

    [Fact]
    public void SecurityCode_Value() =>
        Assert.Equal(0xDEADBEEFu, ControlFrames.Decode<SecurityCode>(Hex.Parse("08 00 0B 00 EF BE AD DE")).Value);

    [Fact]
    public void ReadOnlyItems_HaveWireSize()
    {
        // The decode-only tests above ignore trailing bytes, so pin the sizes of these items explicitly.
        Assert.Equal(2, Unsafe.SizeOf<InterfaceVersion>());
        Assert.Equal(3, Unsafe.SizeOf<FirmwareVersion>());
        Assert.Equal(4, Unsafe.SizeOf<ProductId>());
        Assert.Equal(6, Unsafe.SizeOf<Options>());
        Assert.Equal(4, Unsafe.SizeOf<SecurityCode>());
    }
}
