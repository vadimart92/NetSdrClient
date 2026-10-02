using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Items;

public class VariableItemTests
{
    [Fact]
    public void TargetName_SdrIp()
    {
        Assert.Equal("SDR-IP", ControlFrames.Decode<TargetName>(Hex.Parse("0B 00 01 00 53 44 52 2D 49 50 00")).Value);
        Assert.Equal("SDR-IP", ItemCodec.Read<TargetName>(Hex.Parse("53 44 52 2D 49 50")).Value);
        Assert.Equal("", default(TargetName).Value);
        Assert.Equal(Hex.Parse("0B 00 01 00 53 44 52 2D 49 50 00"), ControlFrames.Reply(ReplyType.Response, new TargetName("SDR-IP")));
        Assert.Throws<ArgumentException>(() => ItemCodec.Write(new TargetName("Вега")));
    }

    [Fact] public void SerialNumber_MT123456() =>
        Assert.Equal("MT123456",
            ControlFrames.Decode<SerialNumber>(Hex.Parse("0D 00 02 00 4D 54 31 32 33 34 35 36 00")).Value);

    [Fact]
    public void StatusCodes_IdleAndOverload()
    {
        Assert.Equal(new[] { StatusCodes.Idle }, ControlFrames.Decode<StatusCodes>(Hex.Parse("05 00 05 00 0B")).Codes);
        Assert.Equal(new[] { StatusCodes.AdOverload }, ControlFrames.Decode<StatusCodes>(Hex.Parse("05 20 05 00 20")).Codes);
        Assert.Throws<ArgumentException>(() => ItemCodec.Write(new StatusCodes([StatusCodes.Idle])));
    }

    [Fact]
    public void FpgaConfiguration_ReadAndSet()
    {
        var config = ControlFrames.Decode<FpgaConfiguration>(Hex.Parse(
            "18 00 0C 00 01 02 09 53 74 64 20 46 50 47 41 20 43 6F 6E 66 69 67 20 00"));
        Assert.Equal((1, 2, 9, "Std FPGA Config "), (config.Selected, config.Id, config.Revision, config.Description));
        Assert.Equal(Hex.Parse("05 00 0C 00 02"), ControlFrames.Request(RequestType.Set, new FpgaConfiguration(2)));
    }

    const string RangesFrame =
        "24 40 20 00 00 02 A0 86 01 00 00 80 CC 06 02 00 00 00 00 00 00 " +
        "00 3B 58 08 00 80 D1 F0 08 00 00 68 89 09 00";

    [Fact]
    public void FrequencyRanges_TwoBands()
    {
        var ranges = ControlFrames.Decode<FrequencyRanges>(Hex.Parse(RangesFrame));
        Assert.Equal(0, ranges.Channel);
        Assert.Equal(
            new[] { new FrequencyRange(100_000, 34_000_000, 0), new FrequencyRange(140_000_000, 150_000_000, 160_000_000) },
            ranges.Ranges);
    }

    [Fact]
    public void FrequencyRanges_Truncated_Throws() =>
        Assert.Throws<ArgumentException>(() => ItemCodec.Read<FrequencyRanges>(Hex.Parse(RangesFrame)[4..21]));

    [Fact]
    public void FrequencyRanges_HeaderOnlyOrEmpty()
    {
        Assert.Empty(ItemCodec.Read<FrequencyRanges>(Hex.Parse("01 00")).Ranges);
        Assert.Throws<ArgumentException>(() => ItemCodec.Read<FrequencyRanges>(Hex.Parse("01")));
        Assert.Empty(default(FrequencyRanges).Ranges);
    }

    [Fact]
    public void SerialNumber_RoundTripsAndStopsAtFirstZero()
    {
        Assert.Equal(Hex.Parse("4D 54 31 00"), ItemCodec.Write(new SerialNumber("MT1")));
        Assert.Equal("MT1", ItemCodec.Read<SerialNumber>(Hex.Parse("4D 54 31 00 58 59")).Value);
        Assert.Equal(Hex.Parse("00"), ItemCodec.Write(default(SerialNumber)));
    }

    [Fact]
    public void FpgaConfiguration_PayloadShorterThanThreeBytes_Throws() =>
        Assert.Throws<ArgumentException>(() => ItemCodec.Read<FpgaConfiguration>(Hex.Parse("01 02")));
}
