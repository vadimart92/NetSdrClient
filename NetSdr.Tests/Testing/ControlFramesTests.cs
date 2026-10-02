using NetSdr.Framing;
using NetSdr.Tests.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class ControlFramesTests
{
    [Fact]
    public void Request_Set_BuildsHeaderCodeAndPayload() =>
        Assert.Equal(Hex.Parse("09 00 50 01 00 2A 00 00 00"),
            ControlFrames.Request(RequestType.Set, new MyVendorItem(0, 42)));

    [Fact]
    public void Reply_Unsolicited_UsesType1() =>
        Assert.Equal(Hex.Parse("09 20 50 01 01 2A 00 00 00"),
            ControlFrames.Reply(ReplyType.Unsolicited, new MyVendorItem(1, 42)));

    [Fact]
    public void Encode_WithoutPayload() =>
        Assert.Equal(Hex.Parse("04 20 01 00"), ControlFrames.Encode((byte)RequestType.Get, 0x0001, []));

    [Fact]
    public void Decode_ReadsItem()
    {
        var item = ControlFrames.Decode<MyVendorItem>(Hex.Parse("09 00 50 01 03 2A 00 00 00"));
        Assert.Equal(3, item.Channel);
        Assert.Equal(42u, item.Value);
    }

    [Theory]
    [InlineData("09 00 51 01 03 2A 00 00 00")]
    [InlineData("0A 00 50 01 03 2A 00 00 00")]
    public void Decode_WrongCodeOrLength_Throws(string hex) =>
        Assert.Throws<ArgumentException>(() => ControlFrames.Decode<MyVendorItem>(Hex.Parse(hex)));

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("01 00")]
    [InlineData("02 00")]
    [InlineData("03 00 50")]
    public void Decode_FrameWithoutRoomForCode_Throws(string hex) =>
        Assert.Throws<ArgumentException>(() => ControlFrames.Decode<MyVendorItem>(Hex.Parse(hex)));

    [Fact]
    public void Decode_IgnoresFrameType() =>
        Assert.Equal(42u, ControlFrames.Decode<MyVendorItem>(Hex.Parse("09 20 50 01 03 2A 00 00 00")).Value);

    [Fact]
    public async Task Eventually_ReturnsOnceConditionHolds()
    {
        int checks = 0;
        await Eventually.ThatAsync(() => ++checks >= 3, Limits.Test);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task Eventually_NonPositiveOrInfiniteTimeout_Throws()
    {
        foreach (TimeSpan timeout in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan })
        {
            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Eventually.ThatAsync(() => true, timeout));
            Assert.Equal("timeout", ex.ParamName);
        }
    }

    [Fact]
    public async Task Eventually_TimesOut() =>
        await Assert.ThrowsAsync<TimeoutException>(() => Eventually.ThatAsync(() => false, TimeSpan.FromMilliseconds(50)));
}
