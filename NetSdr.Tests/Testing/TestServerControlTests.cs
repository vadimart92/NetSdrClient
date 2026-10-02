using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Tests.Items;
using NetSdr.Testing;
using static NetSdr.Tests.Loopback;

namespace NetSdr.Tests.Testing;

public class TestServerControlTests
{
    [Fact]
    public async Task Set_EchoesAndGetReturnsState()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        Assert.Equal(-20, (await client.SetAsync(new RfGain(0, -20))).GainDb);
        Assert.Equal(-20, (await client.GetAsync<RfGain, byte>(0)).GainDb);
    }

    [Fact]
    public async Task Get_MatchesKeyPerChannel()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new ReceiverFrequency(0, 7_000_000));
        await client.SetAsync(new ReceiverFrequency(2, 14_000_000));
        Assert.Equal(7_000_000UL, (ulong)(await client.GetAsync<ReceiverFrequency, byte>(0)).Hz);
        Assert.Equal(14_000_000UL, (ulong)(await client.GetAsync<ReceiverFrequency, byte>(2)).Hz);
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ReceiverFrequency, byte>(1));
    }

    [Fact]
    public async Task UnknownGet_And_GetRange_Nak()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>());
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetRangeAsync<FrequencyRanges, byte>(0));
    }

    [Fact]
    public async Task Preload_ServesGet_And_HandlerOverridesState()
    {
        var (server, client) = await StartAsync(s =>
        {
            s.Preload(new ProductId(0x03524453));
            s.Preload(new InterfaceVersion(529));
            s.OnRequest<InterfaceVersion>(_ => ControlReply.Item(new InterfaceVersion(900)));
        });
        await using var _ = server; await using var __ = client;
        Assert.Equal(0x03524453u, (await client.GetAsync<ProductId>()).Value);
        Assert.Equal(900, (await client.GetAsync<InterfaceVersion>()).Version);
    }

    [Fact]
    public async Task TypedHandler_SeesItemForSet_AndKeyForGet()
    {
        var seen = new ConcurrentQueue<ControlRequest<ReceiverFrequency>>();
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverFrequency>(r =>
        {
            seen.Enqueue(r);
            return r.Type == RequestType.Set
                ? ControlReply.Echo
                : ControlReply.Item(new ReceiverFrequency(r.Key<byte>(), 123));
        }));
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        var got = await client.GetAsync<ReceiverFrequency, byte>(2);
        Assert.Equal((2, 123UL), (got.Channel, (ulong)got.Hz));
        var requests = seen.ToArray();
        Assert.Equal(14_010_000UL, (ulong)requests[0].Item.Hz);
        Assert.Equal(default, requests[1].Item);
    }

    [Fact]
    public async Task RawHandler_Bytes()
    {
        var (server, client) = await StartAsync(s => s.OnRequest(0x0150, _ => ControlReply.Bytes(Hex.Parse("01 2A 00 00 00"))));
        await using var _ = server; await using var __ = client;
        var item = await client.GetAsync<MyVendorItem>();
        Assert.Equal((1, 42u), (item.Channel, item.Value));
    }

    [Fact]
    public async Task Received_RecordsRequestsInOrder()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<TargetName>());
        await client.SetAsync(new RfGain(0, -10));
        var received = server.Received;
        Assert.Equal((RequestType.Get, (ushort)0x0001, 0), (received[0].Type, received[0].Code, received[0].Payload.Length));
        Assert.Equal((RequestType.Set, (ushort)0x0038), (received[1].Type, received[1].Code));
        Assert.Equal(Hex.Parse("00 F6"), received[1].Payload.ToArray());
    }

    [Fact]
    public async Task After_DelaysReply()
    {
        var (server, client) = await StartAsync(s =>
            s.OnRequest<ProductId>(_ => ControlReply.Item(new ProductId(1)).After(TimeSpan.FromMilliseconds(300))));
        await using var _ = server; await using var __ = client;
        var watch = Stopwatch.StartNew();
        await client.GetAsync<ProductId>();
        Assert.True(watch.ElapsedMilliseconds >= 250);
    }

    [Fact]
    public async Task Silent_LeadsToTimeoutFault()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ProductId>(_ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<ProductId>());
        await Assert.ThrowsAsync<TimeoutException>(() => client.Completion.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task SendUnsolicited_ArrivesAtClient()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await server.ClientConnected.WaitAsync(Limits.Test);
        await server.SendUnsolicitedAsync(new AfGain(0, 3));
        await server.SendUnsolicitedAsync(0x0005, Hex.Parse("20"));
        var gain = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var status = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Unsolicited, 3), (gain.Type, gain.As<AfGain>().Level));
        Assert.Equal(new[] { StatusCodes.AdOverload }, status.As<StatusCodes>().Codes);
    }

    [Fact]
    public async Task UnsolicitedFromHandler_BeforeReply_DoesNotBreakRequest()
    {
        NetSdrTestServer? self = null;
        var (server, client) = await StartAsync(s =>
        {
            self = s;
            s.OnRequest<AfGain>(r =>
            {
                self!.SendUnsolicitedAsync(0x0005, Hex.Parse("20")).GetAwaiter().GetResult();
                return ControlReply.Item(new AfGain(r.Key<byte>(), 7));
            });
        });
        await using var _ = server; await using var __ = client;
        Assert.Equal(7, (await client.GetAsync<AfGain, byte>(0)).Level);
        Assert.Equal((ushort)0x0005, (await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code);
    }

    [Fact]
    public async Task DisconnectClient_FailsActiveRequest()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ProductId>(_ => ControlReply.Silent));
        await using var _ = server; await using var __ = client;
        var call = client.GetAsync<ProductId>();
        await Eventually.ThatAsync(() => server.Received.Count == 1);
        await server.DisconnectClientAsync();
        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task SecondClient_ConnectsAfterFirstLeaves()
    {
        var (server, first) = await StartAsync(s => s.Preload(new ProductId(5)));
        await using var _ = server;
        await first.DisposeAsync();
        await using var second = new NetSdrControlClient();
        await second.ConnectAsync("127.0.0.1", server.Port);
        Assert.Equal(5u, (await second.GetAsync<ProductId>()).Value);
    }

    [Fact]
    public async Task ConnectTwice_Throws()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)));
    }

    [Fact]
    public async Task TenConcurrentCalls_AllGetTheirOwnReplies()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverFrequency>(r =>
            ControlReply.Item(new ReceiverFrequency(r.Key<byte>(), 1000UL + r.Key<byte>()))
                .After(TimeSpan.FromMilliseconds(5))));
        await using var _ = server; await using var __ = client;
        var calls = Enumerable.Range(0, 10).Select(ch => client.GetAsync<ReceiverFrequency, byte>((byte)ch)).ToArray();
        var results = await Task.WhenAll(calls).WaitAsync(Limits.Test);
        for (var ch = 0; ch < 10; ch++)
            Assert.Equal((ch, 1000UL + (ulong)ch), (results[ch].Channel, (ulong)results[ch].Hz));
        Assert.Equal(10, server.Received.Count);
    }

    [Fact]
    public async Task Get_AnswersNewestPayloadForTheKey()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new RfGain(0, -20));
        await client.SetAsync(new RfGain(1, -30));
        await client.SetAsync(new RfGain(0, -10));
        Assert.Equal(-10, (await client.GetAsync<RfGain, byte>(0)).GainDb);
        Assert.Equal(-30, (await client.GetAsync<RfGain, byte>(1)).GainDb);
        // An empty key matches any payload, so the newest one wins.
        var newest = await client.SendAsync(RequestType.Get, RfGain.Code, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(Hex.Parse("00 F6"), newest.Payload.ToArray());
    }

    [Fact]
    public async Task State_KeepsSixteenPayloadsPerCode()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        for (byte channel = 0; channel < 17; channel++)
            await client.SetAsync(new ReceiverFrequency(channel, 1000UL + channel));
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ReceiverFrequency, byte>(0));
        Assert.Equal(1001UL, (ulong)(await client.GetAsync<ReceiverFrequency, byte>(1)).Hz);
        Assert.Equal(1016UL, (ulong)(await client.GetAsync<ReceiverFrequency, byte>(16)).Hz);
    }

    [Fact]
    public async Task GetRangeHandler_AnswersWithRangeResponse()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<FrequencyRanges>(r =>
            r.Type == RequestType.GetRange
                ? ControlReply.Bytes(Hex.Parse("00 01 A0 86 01 00 00 80 CC 06 02 00 00 00 00 00 00"))
                : ControlReply.Nak));
        await using var _ = server; await using var __ = client;
        var ranges = await client.GetRangeAsync<FrequencyRanges, byte>(0);
        Assert.Equal(new[] { new FrequencyRange(100_000, 34_000_000, 0) }, ranges.Ranges);
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<FrequencyRanges, byte>(0));
    }

    [Fact]
    public async Task FailingHandler_IsAnsweredWithNak_AndServerKeepsServing()
    {
        var (server, client) = await StartAsync(s =>
        {
            s.OnRequest<ProductId>(_ => throw new InvalidOperationException("handler failure"));
            s.OnRequest<MyVendorItem>(_ => ControlReply.Echo);
            s.Preload(new InterfaceVersion(529));
        });
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>());
        // The typed handler cannot read a Set payload that is too short for its item.
        await Assert.ThrowsAsync<NetSdrNakException>(
            () => client.SendAsync(RequestType.Set, MyVendorItem.Code, Hex.Parse("01")));
        Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task ReplyTooLargeForOneFrame_IsAnsweredWithNak()
    {
        var (server, client) = await StartAsync(s =>
            s.OnRequest(0x0150, _ => ControlReply.Bytes(new byte[FrameHeader.MaxEncodableLength])));
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<MyVendorItem>());
    }

    [Fact]
    public async Task NonControlFramesFromClient_AreIgnored_AndMalformedOnesAreRejected()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        using var raw = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await raw.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        // The server serves one client at a time, so the raw socket is served once the client has left.
        await client.DisposeAsync();
        // A data item, a data acknowledgement, a header-only Get (no item code), then a Get for an unknown item.
        await raw.SendAsync(Hex.Parse("05 80 01 02 03" + "02 60" + "02 20" + "04 20 FF 7F"));
        var reply = new byte[4];
        var read = 0;
        using var timeout = new CancellationTokenSource(Limits.Test);
        while (read < reply.Length)
            read += await raw.ReceiveAsync(reply.AsMemory(read), timeout.Token);
        Assert.Equal(Hex.Parse("02 00 02 00"), reply);
        var received = server.Received.Single();
        Assert.Equal((RequestType.Get, (ushort)0x7FFF), (received.Type, received.Code));
    }

    [Fact]
    public async Task SendUnsolicited_WithoutClient_Throws()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendUnsolicitedAsync(new AfGain(0, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SendUnsolicitedAsync(0x0005, Hex.Parse("20")));
    }

    [Fact]
    public async Task SendUnsolicited_AfterClientLeft_Throws()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await server.ClientConnected.WaitAsync(Limits.Test);
        await client.DisposeAsync();
        await Eventually.ThatAsync(() => !TrySend(server));

        static bool TrySend(NetSdrTestServer s)
        {
            try
            {
                s.SendUnsolicitedAsync(new AfGain(0, 1)).GetAwaiter().GetResult();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (IOException)
            {
                // The server has not noticed yet that the connection is gone; try again.
                return true;
            }
        }
    }

    [Fact]
    public async Task UnsolicitedInParallelWithReplies_NeverInterleavesFrames()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverFrequency>(r =>
            ControlReply.Item(new ReceiverFrequency(r.Key<byte>(), 5000))));
        await using var _ = server; await using var __ = client;
        await server.ClientConnected.WaitAsync(Limits.Test);
        var sends = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => server.SendUnsolicitedAsync(new AfGain(0, (byte)i))))
            .ToArray();
        for (var i = 0; i < 100; i++)
            Assert.Equal(5000UL, (ulong)(await client.GetAsync<ReceiverFrequency, byte>((byte)(i % 3))).Hz);
        await Task.WhenAll(sends).WaitAsync(Limits.Test);
        var levels = new HashSet<byte>();
        for (var i = 0; i < 100; i++)
            levels.Add((await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).As<AfGain>().Level);
        Assert.Equal(100, levels.Count);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task DisconnectClient_ThenNextClientIsServed_AndStateSurvives()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new RfGain(0, -5));
        await server.DisconnectClientAsync();
        await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        await AssertServerFreeAsync(server);
        await using var next = new NetSdrControlClient();
        await next.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.Equal(-5, (await next.GetAsync<RfGain, byte>(0)).GainDb);
    }

    [Fact]
    public async Task Loopback_ReturnsOnlyOnceTheServerHasRegisteredTheClient()
    {
        // Without the registration a disconnect right after the helper returns can find no client and do nothing.
        for (var i = 0; i < 25; i++)
        {
            var (server, client) = await StartAsync();
            await using var _ = server; await using var __ = client;
            await server.DisconnectClientAsync();
            await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        }
    }

    [Fact]
    public async Task DisconnectClient_WithoutClient_DoesNothing()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await server.DisconnectClientAsync();
    }

    [Fact]
    public async Task DisposeServer_ClosesClient_AndStopsListening()
    {
        var (server, client) = await StartAsync();
        await using var __ = client;
        await server.ClientConnected.WaitAsync(Limits.Test);
        await server.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        await server.DisposeAsync();
        await using var late = new NetSdrControlClient();
        await Assert.ThrowsAsync<SocketException>(
            () => late.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)));
    }

    [Fact]
    public async Task DisposeServer_BeforeAnyClient_CancelsClientConnected()
    {
        var server = new NetSdrTestServer();
        await server.StartAsync();
        var connected = server.ClientConnected;
        await server.DisposeAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => connected.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync());
    }

    [Fact]
    public async Task Start_Twice_Throws()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2_147_483_648)]
    public void After_RejectsDelayOutsideTheTimerRange(double milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ControlReply.Echo.After(TimeSpan.FromMilliseconds(milliseconds)));
}
