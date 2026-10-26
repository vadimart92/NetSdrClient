using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Items;

public class VariableItemTests
{
    [Test]
    public void TargetName_SdrIp()
    {
        Assert.That(ControlFrames.Decode<TargetName>(Hex.Parse("0B 00 01 00 53 44 52 2D 49 50 00")).Value, Is.EqualTo("SDR-IP"));
        Assert.That(ItemCodec.Read<TargetName>(Hex.Parse("53 44 52 2D 49 50")).Value, Is.EqualTo("SDR-IP"));
        Assert.That(default(TargetName).Value, Is.EqualTo(""));
        Assert.That(
            ControlFrames.Reply(ReplyType.Response, new TargetName("SDR-IP")),
            Is.EqualTo(Hex.Parse("0B 00 01 00 53 44 52 2D 49 50 00")));
        Assert.That(() => ItemCodec.Write(new TargetName("Вега")), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void SerialNumber_MT123456() =>
        Assert.That(ControlFrames.Decode<SerialNumber>(Hex.Parse("0D 00 02 00 4D 54 31 32 33 34 35 36 00")).Value, Is.EqualTo("MT123456"));

    [Test]
    public void StatusCodes_IdleAndOverload()
    {
        Assert.That(ControlFrames.Decode<StatusCodes>(Hex.Parse("05 00 05 00 0B")).Codes, Is.EqualTo(new[] { StatusCodes.Idle }));
        Assert.That(ControlFrames.Decode<StatusCodes>(Hex.Parse("05 20 05 00 20")).Codes, Is.EqualTo(new[] { StatusCodes.AdOverload }));
        Assert.That(() => ItemCodec.Write(new StatusCodes([StatusCodes.Idle])), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void FpgaConfiguration_ReadAndSet()
    {
        var config = ControlFrames.Decode<FpgaConfiguration>(Hex.Parse(
            "18 00 0C 00 01 02 09 53 74 64 20 46 50 47 41 20 43 6F 6E 66 69 67 20 00"));
        Assert.That((config.Selected, config.Id, config.Revision, config.Description), Is.EqualTo((1, 2, 9, "Std FPGA Config ")));
        Assert.That(ControlFrames.Request(RequestType.Set, new FpgaConfiguration(2)), Is.EqualTo(Hex.Parse("05 00 0C 00 02")));
    }

    private const string RangesFrame =
        "24 40 20 00 00 02 A0 86 01 00 00 80 CC 06 02 00 00 00 00 00 00 " +
        "00 3B 58 08 00 80 D1 F0 08 00 00 68 89 09 00";

    [Test]
    public void FrequencyRanges_TwoBands()
    {
        var ranges = ControlFrames.Decode<FrequencyRanges>(Hex.Parse(RangesFrame));
        Assert.That(ranges.Channel, Is.EqualTo(0));
        Assert.That(
            ranges.Ranges,
            Is.EqualTo(new[] { new FrequencyRange(100_000, 34_000_000, 0), new FrequencyRange(140_000_000, 150_000_000, 160_000_000) }));
    }

    [Test]
    public void FrequencyRanges_Truncated_Throws() =>
        Assert.That(() => ItemCodec.Read<FrequencyRanges>(Hex.Parse(RangesFrame)[4..21]), Throws.TypeOf<ArgumentException>());

    [Test]
    public void FrequencyRanges_HeaderOnlyOrEmpty()
    {
        Assert.That(ItemCodec.Read<FrequencyRanges>(Hex.Parse("01 00")).Ranges, Is.Empty);
        Assert.That(() => ItemCodec.Read<FrequencyRanges>(Hex.Parse("01")), Throws.TypeOf<ArgumentException>());
        Assert.That(default(FrequencyRanges).Ranges, Is.Empty);
    }

    [Test]
    public void SerialNumber_RoundTripsAndStopsAtFirstZero()
    {
        Assert.That(ItemCodec.Write(new SerialNumber("MT1")), Is.EqualTo(Hex.Parse("4D 54 31 00")));
        Assert.That(ItemCodec.Read<SerialNumber>(Hex.Parse("4D 54 31 00 58 59")).Value, Is.EqualTo("MT1"));
        Assert.That(ItemCodec.Write(default(SerialNumber)), Is.EqualTo(Hex.Parse("00")));
    }

    [Test]
    public void FpgaConfiguration_PayloadShorterThanThreeBytes_Throws() =>
        Assert.That(() => ItemCodec.Read<FpgaConfiguration>(Hex.Parse("01 02")), Throws.TypeOf<ArgumentException>());
}
