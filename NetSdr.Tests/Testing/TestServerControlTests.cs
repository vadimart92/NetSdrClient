using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Items;
using static NetSdr.Tests.Loopback;

namespace NetSdr.Tests.Testing;

public class TestServerControlTests
{
    [Test]
    public async Task Set_EchoesAndGetReturnsState()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        Assert.That((await client.SetAsync(new RfGain(0, -20))).GainDb, Is.EqualTo(-20));
        Assert.That((await client.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-20));
    }

    [Test]
    public async Task Get_MatchesKeyPerChannel()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(new ReceiverFrequency(0, 7_000_000));
        await client.SetAsync(new ReceiverFrequency(2, 14_000_000));
        Assert.That((ulong)(await client.GetAsync<ReceiverFrequency, byte>(0)).Hz, Is.EqualTo(7_000_000UL));
        Assert.That((ulong)(await client.GetAsync<ReceiverFrequency, byte>(2)).Hz, Is.EqualTo(14_000_000UL));
        await Assert.ThatAsync(() => client.GetAsync<ReceiverFrequency, byte>(1), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task UnknownGet_And_GetRange_Nak()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await Assert.ThatAsync(() => client.GetAsync<ProductId>(), Throws.TypeOf<NetSdrNakException>());
        await Assert.ThatAsync(() => client.GetRangeAsync<FrequencyRanges, byte>(0), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task Preload_ServesGet_And_HandlerOverridesState()
    {
        var (server, client) = await StartAsync(s =>
        {
            s.Preload(new ProductId(0x03524453));
            s.Preload(new InterfaceVersion(529));
            s.OnRequest<InterfaceVersion>(_ => ControlReply.Item(new InterfaceVersion(900)));
        });
        await using var _ = server;
        await using var __ = client;
        Assert.That((await client.GetAsync<ProductId>()).Value, Is.EqualTo(0x03524453u));
        Assert.That((await client.GetAsync<InterfaceVersion>()).Version, Is.EqualTo(900));
    }

    [Test]
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
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        var got = await client.GetAsync<ReceiverFrequency, byte>(2);
        Assert.That((got.Channel, (ulong)got.Hz), Is.EqualTo((2, 123UL)));
        var requests = seen.ToArray();
        Assert.That((ulong)requests[0].Item.Hz, Is.EqualTo(14_010_000UL));
        Assert.That(requests[1].Item, Is.Default);
    }

    [Test]
    public async Task RawHandler_Bytes()
    {
        var (server, client) = await StartAsync(s => s.OnRequest(0x0150, _ => ControlReply.Bytes(Hex.Parse("01 2A 00 00 00"))));
        await using var _ = server;
        await using var __ = client;
        var item = await client.GetAsync<MyVendorItem>();
        Assert.That((item.Channel, item.Value), Is.EqualTo((1, 42u)));
    }

    [Test]
    public async Task Received_RecordsRequestsInOrder()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await Assert.ThatAsync(() => client.GetAsync<TargetName>(), Throws.TypeOf<NetSdrNakException>());
        await client.SetAsync(new RfGain(0, -10));
        var received = server.Received;
        Assert.That((received[0].Type, received[0].Code, received[0].Payload.Length), Is.EqualTo((RequestType.Get, (ushort)0x0001, 0)));
        Assert.That((received[1].Type, received[1].Code), Is.EqualTo((RequestType.Set, (ushort)0x0038)));
        Assert.That(received[1].Payload.ToArray(), Is.EqualTo(Hex.Parse("00 F6")));
    }

    [Test]
    public async Task After_DelaysReply()
    {
        var (server, client) = await StartAsync(s =>
            s.OnRequest<ProductId>(_ => ControlReply.Item(new ProductId(1)).After(TimeSpan.FromMilliseconds(300))));
        await using var _ = server;
        await using var __ = client;
        var watch = Stopwatch.StartNew();
        await client.GetAsync<ProductId>();
        Assert.That(watch.ElapsedMilliseconds >= 250, Is.True);
    }

    [Test]
    public async Task Silent_LeadsToTimeoutFault()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ProductId>(_ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = server;
        await using var __ = client;
        await Assert.ThatAsync(() => client.GetAsync<ProductId>(), Throws.TypeOf<TimeoutException>());
        await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task SendUnsolicited_ArrivesAtClient()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await server.ClientConnected.WaitAsync(Limits.Test);
        await server.SendUnsolicitedAsync(new AfGain(0, 3));
        await server.SendUnsolicitedAsync(0x0005, Hex.Parse("20"));
        var gain = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var status = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That((gain.Type, gain.As<AfGain>().Level), Is.EqualTo((ReplyType.Unsolicited, 3)));
        Assert.That(status.As<StatusCodes>().Codes, Is.EqualTo(new[] { StatusCodes.AdOverload }));
    }

    [Test]
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
        await using var _ = server;
        await using var __ = client;
        Assert.That((await client.GetAsync<AfGain, byte>(0)).Level, Is.EqualTo(7));
        Assert.That((await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code, Is.EqualTo((ushort)0x0005));
    }

    [Test]
    public async Task DisconnectClient_FailsActiveRequest()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ProductId>(_ => ControlReply.Silent));
        await using var _ = server;
        await using var __ = client;
        var call = client.GetAsync<ProductId>();
        await Eventually.ThatAsync(() => server.Received.Count == 1);
        await server.DisconnectClientAsync();
        await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
    }

    [Test]
    public async Task SecondClient_ConnectsAfterFirstLeaves()
    {
        var (server, first) = await StartAsync(s => s.Preload(new ProductId(5)));
        await using var _ = server;
        await first.DisposeAsync();
        await using var second = new NetSdrControlClient();
        await second.ConnectAsync("127.0.0.1", server.Port);
        Assert.That((await second.GetAsync<ProductId>()).Value, Is.EqualTo(5u));
    }

    [Test]
    public async Task ConnectTwice_Throws()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await Assert.ThatAsync(
            () => client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task TenConcurrentCalls_AllGetTheirOwnReplies()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverFrequency>(r =>
            ControlReply.Item(new ReceiverFrequency(r.Key<byte>(), 1000UL + r.Key<byte>()))
                .After(TimeSpan.FromMilliseconds(5))));
        await using var _ = server;
        await using var __ = client;
        var calls = Enumerable.Range(0, 10).Select(ch => client.GetAsync<ReceiverFrequency, byte>((byte)ch)).ToArray();
        var results = await Task.WhenAll(calls).WaitAsync(Limits.Test);
        for (var ch = 0; ch < 10; ch++)
            Assert.That((results[ch].Channel, (ulong)results[ch].Hz), Is.EqualTo((ch, 1000UL + (ulong)ch)));
        Assert.That(server.Received, Has.Count.EqualTo(10));
    }

    [Test]
    public async Task Get_AnswersNewestPayloadForTheKey()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(new RfGain(0, -20));
        await client.SetAsync(new RfGain(1, -30));
        await client.SetAsync(new RfGain(0, -10));
        Assert.That((await client.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-10));
        Assert.That((await client.GetAsync<RfGain, byte>(1)).GainDb, Is.EqualTo(-30));
        // An empty key matches any payload, so the newest one wins.
        var newest = await client.SendAsync(RequestType.Get, RfGain.Code, ReadOnlyMemory<byte>.Empty);
        Assert.That(newest.Payload.ToArray(), Is.EqualTo(Hex.Parse("00 F6")));
    }

    [Test]
    public async Task State_KeepsSixteenPayloadsPerCode()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        for (byte channel = 0; channel < 17; channel++)
            await client.SetAsync(new ReceiverFrequency(channel, 1000UL + channel));
        await Assert.ThatAsync(() => client.GetAsync<ReceiverFrequency, byte>(0), Throws.TypeOf<NetSdrNakException>());
        Assert.That((ulong)(await client.GetAsync<ReceiverFrequency, byte>(1)).Hz, Is.EqualTo(1001UL));
        Assert.That((ulong)(await client.GetAsync<ReceiverFrequency, byte>(16)).Hz, Is.EqualTo(1016UL));
    }

    [Test]
    public async Task GetRangeHandler_AnswersWithRangeResponse()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<FrequencyRanges>(r =>
            r.Type == RequestType.GetRange
                ? ControlReply.Item(new FrequencyRanges(r.Key<byte>(), [new FrequencyRange(100_000, 34_000_000, 0)]))
                : ControlReply.Nak));
        await using var _ = server;
        await using var __ = client;
        var ranges = await client.GetRangeAsync<FrequencyRanges, byte>(0);
        Assert.That(ranges.Ranges, Is.EqualTo(new[] { new FrequencyRange(100_000, 34_000_000, 0) }));
        await Assert.ThatAsync(() => client.GetAsync<FrequencyRanges, byte>(0), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task FailingHandler_IsAnsweredWithNak_AndServerKeepsServing()
    {
        var (server, client) = await StartAsync(s =>
        {
            s.OnRequest<ProductId>(_ => throw new InvalidOperationException("handler failure"));
            s.OnRequest<MyVendorItem>(_ => ControlReply.Echo);
            s.Preload(new InterfaceVersion(529));
        });
        await using var _ = server;
        await using var __ = client;
        await Assert.ThatAsync(() => client.GetAsync<ProductId>(), Throws.TypeOf<NetSdrNakException>());
        // The typed handler cannot read a Set payload that is too short for its item.
        await Assert.ThatAsync(
            () => client.SendAsync(RequestType.Set, MyVendorItem.Code, Hex.Parse("01")),
            Throws.TypeOf<NetSdrNakException>());
        Assert.That((await client.GetAsync<InterfaceVersion>()).Version, Is.EqualTo(529));
        Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task FailingHandler_IsRecordedInHandlerErrors()
    {
        var failure = new InvalidOperationException("handler failure");
        var (server, client) = await StartAsync(s => s.OnRequest<ProductId>(_ => throw failure));
        await using var _ = server;
        await using var __ = client;
        Assert.That(server.HandlerErrors, Is.Empty);

        await Assert.ThatAsync(() => client.GetAsync<ProductId>(), Throws.TypeOf<NetSdrNakException>());
        Assert.That(server.HandlerErrors.Sole(), Is.SameAs(failure));

        // A NAK the server chooses to send is not an error: nothing was thrown.
        await Assert.ThatAsync(() => client.GetAsync<InterfaceVersion>(), Throws.TypeOf<NetSdrNakException>());
        Assert.That(server.HandlerErrors, Has.Exactly(1).Items);
    }

    [Test]
    public async Task ReplyTooLargeForOneFrame_IsAnsweredWithNak()
    {
        var (server, client) = await StartAsync(s =>
            s.OnRequest(0x0150, _ => ControlReply.Bytes(new byte[FrameHeader.MaxEncodableLength])));
        await using var _ = server;
        await using var __ = client;
        await Assert.ThatAsync(() => client.GetAsync<MyVendorItem>(), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task NonControlFramesFromClient_AreIgnored_AndMalformedOnesAreRejected()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
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
        Assert.That(reply, Is.EqualTo(Hex.Parse("02 00 02 00")));
        var received = server.Received.Single();
        Assert.That((received.Type, received.Code), Is.EqualTo((RequestType.Get, (ushort)0x7FFF)));
    }

    [Test]
    public async Task SendUnsolicited_WithoutClient_Throws()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await Assert.ThatAsync(() => server.SendUnsolicitedAsync(new AfGain(0, 1)), Throws.TypeOf<InvalidOperationException>());
        await Assert.ThatAsync(() => server.SendUnsolicitedAsync(0x0005, Hex.Parse("20")), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
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

    [Test]
    public async Task UnsolicitedInParallelWithReplies_NeverInterleavesFrames()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverFrequency>(r =>
            ControlReply.Item(new ReceiverFrequency(r.Key<byte>(), 5000))));
        await using var _ = server;
        await using var __ = client;
        await server.ClientConnected.WaitAsync(Limits.Test);
        var sends = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => server.SendUnsolicitedAsync(new AfGain(0, (byte)i))))
            .ToArray();
        for (var i = 0; i < 100; i++)
            Assert.That((ulong)(await client.GetAsync<ReceiverFrequency, byte>((byte)(i % 3))).Hz, Is.EqualTo(5000UL));
        await Task.WhenAll(sends).WaitAsync(Limits.Test);
        var levels = new HashSet<byte>();
        for (var i = 0; i < 100; i++)
            levels.Add((await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).As<AfGain>().Level);
        Assert.That(levels, Has.Count.EqualTo(100));
        Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task DisconnectClient_ThenNextClientIsServed_AndStateSurvives()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(new RfGain(0, -5));
        await server.DisconnectClientAsync();
        await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        await AssertServerFreeAsync(server);
        await using var next = new NetSdrControlClient();
        await next.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.That((await next.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-5));
    }

    [Test]
    public async Task Loopback_ReturnsOnlyOnceTheServerHasRegisteredTheClient()
    {
        // Without the registration a disconnect right after the helper returns can find no client and do nothing.
        for (var i = 0; i < 25; i++)
        {
            var (server, client) = await StartAsync();
            await using var _ = server;
            await using var __ = client;
            await server.DisconnectClientAsync();
            await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        }
    }

    [Test]
    public async Task DisconnectClient_WithoutClient_DoesNothing()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await Assert.ThatAsync(() => server.DisconnectClientAsync(), Throws.Nothing);
    }

    [Test]
    public async Task DisposeServer_ClosesClient_AndStopsListening()
    {
        var (server, client) = await StartAsync();
        await using var __ = client;
        await server.ClientConnected.WaitAsync(Limits.Test);
        await server.DisposeAsync();
        await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        await server.DisposeAsync();
        await using var late = new NetSdrControlClient();
        await Assert.ThatAsync(() => late.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)), Throws.TypeOf<SocketException>());
    }

    [Test]
    public async Task DisposeServer_BeforeAnyClient_CancelsClientConnected()
    {
        var server = new NetSdrTestServer();
        await server.StartAsync();
        var connected = server.ClientConnected;
        await server.DisposeAsync();
        await Assert.ThatAsync(() => connected.WaitAsync(Limits.Test), Throws.TypeOf<TaskCanceledException>());
        await Assert.ThatAsync(() => server.StartAsync(), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public async Task Start_Twice_Throws()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await Assert.ThatAsync(() => server.StartAsync(), Throws.TypeOf<InvalidOperationException>());
    }

    [TestCase(-1)]
    [TestCase(2_147_483_648)]
    public void After_RejectsDelayOutsideTheTimerRange(double milliseconds) =>
        Assert.That(() => ControlReply.Echo.After(TimeSpan.FromMilliseconds(milliseconds)), Throws.TypeOf<ArgumentOutOfRangeException>());
}
