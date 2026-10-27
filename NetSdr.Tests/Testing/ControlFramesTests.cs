using NetSdr.Framing;
using NetSdr.Testing;
using NetSdr.Tests.Items;

namespace NetSdr.Tests.Testing;

public class ControlFramesTests
{
    [Test]
    public void Request_Set_BuildsHeaderCodeAndPayload() =>
        Assert.That(ControlFrames.Request(RequestType.Set, new MyVendorItem(0, 42)), Is.EqualTo(Hex.Parse("09 00 50 01 00 2A 00 00 00")));

    [Test]
    public void Reply_Unsolicited_UsesType1() =>
        Assert.That(
            ControlFrames.Reply(ReplyType.Unsolicited, new MyVendorItem(1, 42)),
            Is.EqualTo(Hex.Parse("09 20 50 01 01 2A 00 00 00")));

    [Test]
    public void Encode_WithoutPayload() =>
        Assert.That(ControlFrames.Encode((byte)RequestType.Get, 0x0001, []), Is.EqualTo(Hex.Parse("04 20 01 00")));

    [Test]
    public void Decode_ReadsItem()
    {
        var item = ControlFrames.Decode<MyVendorItem>(Hex.Parse("09 00 50 01 03 2A 00 00 00"));
        Assert.That(item.Channel, Is.EqualTo(3));
        Assert.That(item.Value, Is.EqualTo(42u));
    }

    [TestCase("09 00 51 01 03 2A 00 00 00")]
    [TestCase("0A 00 50 01 03 2A 00 00 00")]
    public void Decode_WrongCodeOrLength_Throws(string hex) =>
        Assert.That(() => ControlFrames.Decode<MyVendorItem>(Hex.Parse(hex)), Throws.TypeOf<ArgumentException>());

    [TestCase("")]
    [TestCase("00")]
    [TestCase("01 00")]
    [TestCase("02 00")]
    [TestCase("03 00 50")]
    public void Decode_FrameWithoutRoomForCode_Throws(string hex) =>
        Assert.That(() => ControlFrames.Decode<MyVendorItem>(Hex.Parse(hex)), Throws.TypeOf<ArgumentException>());

    [Test]
    public void Decode_IgnoresFrameType() =>
        Assert.That(ControlFrames.Decode<MyVendorItem>(Hex.Parse("09 20 50 01 03 2A 00 00 00")).Value, Is.EqualTo(42u));

    [Test]
    public async Task Eventually_ReturnsOnceConditionHolds()
    {
        int checks = 0;
        await Eventually.ThatAsync(() => ++checks >= 3, Limits.Test);
        Assert.That(checks, Is.EqualTo(3));
    }

    [Test]
    public async Task Eventually_NonPositiveOrInfiniteTimeout_Throws()
    {
        foreach (TimeSpan timeout in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan })
        {
            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Eventually.ThatAsync(() => true, timeout));
            Assert.That(ex.ParamName, Is.EqualTo("timeout"));
        }
    }

    [Test]
    public async Task Eventually_TimesOut() =>
        await Assert.ThatAsync(() => Eventually.ThatAsync(() => false, TimeSpan.FromMilliseconds(50)), Throws.TypeOf<TimeoutException>());
}
