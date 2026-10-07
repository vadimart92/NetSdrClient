using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Tests.Control;

public class ControlClientProtocolTests
{
    private const string Freq14 = "0A 00 20 00 00 90 C6 D5 00 00";
    private const string RangesFrame =
        "24 40 20 00 00 02 A0 86 01 00 00 80 CC 06 02 00 00 00 00 00 00 " +
        "00 3B 58 08 00 80 D1 F0 08 00 00 68 89 09 00";

    [Fact]
    public async Task SetAsync_SendsFrameAndReturnsEcho()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        Assert.Equal(Hex.Parse(Freq14), await device.ReadRequestAsync());
        await device.SendAsync(Freq14);
        Assert.Equal(14_010_000UL, (ulong)(await call.WaitAsync(Limits.Test)).Hz);
    }

    [Fact]
    public async Task GetAsync_WithByteKey()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        Assert.Equal(Hex.Parse("05 20 20 00 00"), await device.ReadRequestAsync());
        await device.SendAsync(Freq14);
        Assert.Equal(14_010_000UL, (ulong)(await call.WaitAsync(Limits.Test)).Hz);
    }

    [Fact]
    public async Task GetAsync_WithUIntKey()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<SecurityCode, uint>(0x12345678);
        Assert.Equal(Hex.Parse("08 20 0B 00 78 56 34 12"), await device.ReadRequestAsync());
        await device.SendAsync("08 00 0B 00 EF BE AD DE");
        Assert.Equal(0xDEADBEEFu, (await call.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task GetAsync_WithoutKey()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<TargetName>();
        Assert.Equal(Hex.Parse("04 20 01 00"), await device.ReadRequestAsync());
        await device.SendAsync("0B 00 01 00 53 44 52 2D 49 50 00");
        Assert.Equal("SDR-IP", (await call.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task GetRangeAsync_ExpectsRangeResponse()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetRangeAsync<FrequencyRanges, byte>(0);
        Assert.Equal(Hex.Parse("05 40 20 00 00"), await device.ReadRequestAsync());
        await device.SendAsync(RangesFrame);
        Assert.Equal(2, (await call.WaitAsync(Limits.Test)).Ranges.Length);
    }

    [Fact]
    public async Task SendAsync_Raw_ReturnsMessage()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.SendAsync(RequestType.Get, 0x0005, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(Hex.Parse("04 20 05 00"), await device.ReadRequestAsync());
        await device.SendAsync("05 00 05 00 0B");
        var message = await call.WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0x0005), (message.Type, message.Code));
        Assert.Equal(new byte[] { 0x0B }, message.Payload.ToArray());
    }

    [Theory]
    [InlineData(RequestType.DataAck)]
    [InlineData(RequestType.Data0)]
    public async Task SendAsync_NonControlType_Throws(RequestType type)
    {
        await using var device = PipeDevice.Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.Client.SendAsync(type, 1, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task Nak_FailsRequest()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("02 00");
        var ex = await Assert.ThrowsAsync<NetSdrNakException>(() => call.WaitAsync(Limits.Test));
        Assert.Equal(((ushort)0x0009, RequestType.Get), (ex.Code, ex.RequestType));
    }

    [Fact]
    public async Task ForeignCode_FailsRequest_GoesToUnsolicited_ConnectionAlive()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("06 00 03 00 11 02");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
        var message = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0x0003), (message.Type, message.Code));
        Assert.True(device.Client.IsConnected);
    }

    [Fact]
    public async Task WrongReplyType_FailsRequest()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("0A 40 20 00 00 90 C6 D5 00 00");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task ShortPayload_FailsRequest_ConnectionAlive()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("07 00 20 00 00 90 C6");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
        var next = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(Freq14);
        Assert.Equal(14_010_000UL, (ulong)(await next.WaitAsync(Limits.Test)).Hz);
    }

    [Fact]
    public async Task UnsolicitedDuringRequest_IsNotTakenAsResponse()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("06 20 48 00 00 03");
        await device.SendAsync("06 00 48 00 00 0A");
        Assert.Equal(10, (await call.WaitAsync(Limits.Test)).Level);
        var message = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal(ReplyType.Unsolicited, message.Type);
        Assert.True(message.Is<AfGain>());
        Assert.Equal(3, message.As<AfGain>().Level);
    }

    [Fact]
    public async Task ResponseWithoutRequest_GoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("05 00 05 00 0B");
        var message = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0x0005), (message.Type, message.Code));
    }

    [Fact]
    public async Task DataAckAndDataItems_GoToUnsolicitedWithCodeZero()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("03 60 02");
        await device.SendAsync("06 80 01 02 03 04");
        var ack = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var data = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.DataAck, (ushort)0), (ack.Type, ack.Code));
        Assert.Equal(new byte[] { 2 }, ack.Payload.ToArray());
        Assert.Equal((ReplyType.Data0, (ushort)0), (data.Type, data.Code));
        Assert.Equal(Hex.Parse("01 02 03 04"), data.Payload.ToArray());
    }

    [Fact]
    public async Task UnsolicitedOverflow_DropsOldest()
    {
        await using var device = PipeDevice.Create(new NetSdrControlClientOptions { UnsolicitedCapacity = 2 });
        await device.SendAsync("06 20 48 00 00 01");
        await device.SendAsync("06 20 48 00 00 02");
        await device.SendAsync("06 20 48 00 00 03");
        var sync = device.Client.GetAsync<ProductId>();          // reader handles frames in order,
        await device.ReadRequestAsync();                          // so after this reply all three
        await device.SendAsync("08 00 09 00 53 44 52 03");        // unsolicited frames were processed
        await sync.WaitAsync(Limits.Test);
        Assert.True(device.Client.Unsolicited.TryRead(out var first));
        Assert.True(device.Client.Unsolicited.TryRead(out var second));
        Assert.False(device.Client.Unsolicited.TryRead(out _));
        Assert.Equal((2, 3), (first.As<AfGain>().Level, second.As<AfGain>().Level));
    }

    [Fact]
    public async Task Frame_OneByteAtATime()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<TargetName>();
        await device.ReadRequestAsync();
        await device.SendBytewiseAsync("0B 00 01 00 53 44 52 2D 49 50 00");
        Assert.Equal("SDR-IP", (await call.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task TwoFramesInOneWrite()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("06 20 48 00 00 03 06 00 48 00 00 0A");
        Assert.Equal(10, (await call.WaitAsync(Limits.Test)).Level);
        Assert.Equal(3, (await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).As<AfGain>().Level);
    }

    [Fact]
    public async Task Frame_AcrossPipeSegments()
    {
        await using var device = PipeDevice.Create(toClient: new PipeOptions(minimumSegmentSize: 16));
        var call = device.Client.GetRangeAsync<FrequencyRanges, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(RangesFrame);
        var ranges = await call.WaitAsync(Limits.Test);
        Assert.Equal(new FrequencyRange(140_000_000, 150_000_000, 160_000_000), ranges.Ranges[1]);
    }

    [Fact]
    public async Task BrokenHeader_FaultsClient()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("01 00");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        Assert.False(device.Client.IsConnected);
    }

    [Fact]
    public async Task ControlFrameOfLength3_FaultsClient()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("03 00 AA");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        Assert.False(device.Client.IsConnected);
    }

    [Fact]
    public async Task NakWithoutRequest_GoesToUnsolicitedWithCodeZero()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("02 00");
        var message = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0), (message.Type, message.Code));
        Assert.True(message.Payload.IsEmpty);
    }

    [Fact]
    public async Task Fault_CompletesUnsolicitedWithoutError()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("01 00");
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        Assert.True(device.Client.Unsolicited.Completion.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => device.Client.Completion.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task OversizedItem_NamesTheItemParameter()
    {
        await using var device = PipeDevice.Create();
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.Client.SetAsync(new HugeItem()));
        Assert.Equal("item", ex.ParamName);
    }

    [Fact]
    public async Task OversizedKey_NamesTheKeyParameter()
    {
        await using var device = PipeDevice.Create();
        var get = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.Client.GetAsync<ProductId, HugeKey>(default));
        var range = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.Client.GetRangeAsync<ProductId, HugeKey>(default));
        Assert.Equal(("key", "key"), (get.ParamName, range.ParamName));
    }

    [Fact]
    public async Task OversizedPayload_NamesThePayloadParameter()
    {
        await using var device = PipeDevice.Create();
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.Client.SendAsync(RequestType.Set, 0x0150, new byte[8188]));
        Assert.Equal("payload", ex.ParamName);
    }

    [Fact]
    public async Task OversizedPayload_ThrowsBeforeWrite()
    {
        await using var device = PipeDevice.Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.Client.SendAsync(RequestType.Set, 0x0150, new byte[8188]));
        var call = device.Client.SetAsync(new RfGain(0, -20));
        Assert.Equal(Hex.Parse("06 00 38 00 00 EC"), await device.ReadRequestAsync());
        await device.SendAsync("06 00 38 00 00 EC");
        Assert.Equal(-20, (await call.WaitAsync(Limits.Test)).GainDb);
    }

    [Fact]
    public async Task SetAsync_ItemThatWritesLessThanItsSize_DoesNotLeakPooledBytes()
    {
        await using var device = PipeDevice.Create();
        // Each round first sends an item that fills its payload, so the pooled buffer the silent item rents next
        // is likely to hold the filler bytes.
        for (var round = 0; round < 50; round++)
        {
            var filled = device.Client.SetAsync(new FillerItem(0xAAAA));
            Assert.Equal(Hex.Parse("06 00 01 7F AA AA"), await device.ReadRequestAsync());
            await device.SendAsync("06 00 01 7F AA AA");
            await filled.WaitAsync(Limits.Test);

            var silent = device.Client.SetAsync(new SilentItem(0));
            Assert.Equal(Hex.Parse("06 00 02 7F 00 00"), await device.ReadRequestAsync());
            await device.SendAsync("06 00 02 7F 00 00");
            await silent.WaitAsync(Limits.Test);
        }
    }

    /// <summary>An item that writes its whole 2-byte payload.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct FillerItem(ushort value) : IControlItem<FillerItem>
    {
        public readonly ushort Value = value;

        public static ushort Code => 0x7F01;

        public static void Write(in FillerItem item, Span<byte> destination) => destination[..2].Fill(0xAA);
    }

    /// <summary>A faulty item: it promises 2 payload bytes and writes none of them.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct SilentItem(ushort value) : IControlItem<SilentItem>
    {
        public readonly ushort Value = value;

        public static ushort Code => 0x7F02;

        public static void Write(in SilentItem item, Span<byte> destination)
        {
        }
    }

    /// <summary>An item whose payload cannot fit in a frame.</summary>
    private readonly struct HugeItem : IControlItem<HugeItem>
    {
        public static ushort Code => 0x7001;

        public static int GetSize(in HugeItem item) => 9000;

        public static void Write(in HugeItem item, Span<byte> destination)
        {
        }
    }

    /// <summary>A key that cannot fit in a frame.</summary>
    [InlineArray(9000)]
    private struct HugeKey
    {
        private byte _element;
    }
}
