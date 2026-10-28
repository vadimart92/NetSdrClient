using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Data;
using NetSdr.Tests.Items;
using static NetSdr.Tests.Loopback;

namespace NetSdr.Tests.Testing;

public class TestServerBuiltinTests
{
    [Test]
    public async Task Builtin_BehavesAsIfNoHandlerWereSet()
    {
        var seen = new ConcurrentQueue<RequestType>();
        var (server, client) = await StartAsync(s => s.OnRequest<RfGain>(r =>
        {
            seen.Enqueue(r.Type);
            return ControlReply.Builtin();
        }));
        await using var _ = server;
        await using var __ = client;

        await Assert.ThatAsync(() => client.GetAsync<RfGain, byte>(0), Throws.TypeOf<NetSdrNakException>());
        Assert.That((await client.SetAsync(new RfGain(0, -20))).GainDb, Is.EqualTo(-20));
        Assert.That((await client.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-20));
        Assert.That(seen, Is.EqualTo(new[] { RequestType.Get, RequestType.Set, RequestType.Get }));
        Assert.That(server.HandlerErrors, Is.Empty);
    }

    [Test]
    public async Task BuiltinWithItem_StoresAndEchoesTheItem_AndRecordsWhatTheClientSent()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<RfGain>(r => r.Type == RequestType.Set
            ? ControlReply.Builtin(new RfGain(r.Item.Channel, -30))
            : ControlReply.Builtin()));
        await using var _ = server;
        await using var __ = client;

        Assert.That((await client.SetAsync(new RfGain(0, -10))).GainDb, Is.EqualTo(-30));
        Assert.That((await client.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-30));
        Assert.That(server.Received[0].Payload.ToArray(), Is.EqualTo(Hex.Parse("00 F6")));
    }

    [Test]
    public async Task BuiltinWithUdpAddress_SendsTheStreamToThePatchedPort()
    {
        using var c = new PacketCollector();
        var (server, client) = await StartAsync(s => s.OnRequest<DataOutputUdpAddress>(r => r.Type == RequestType.Set
            ? ControlReply.Builtin(new DataOutputUdpAddress(r.Item.Ip, (ushort)c.EndPoint.Port))
            : ControlReply.Builtin()));
        await using var _ = server;
        await using var __ = client;

        // The client asks for a port nobody listens on; the server sends to the collector all the same.
        var asked = new IPEndPoint(IPAddress.Loopback, c.EndPoint.Port == 1 ? 2 : 1);
        await client.SetAsync(DataOutputUdpAddress.For(asked));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));

        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        Assert.That((await client.GetAsync<DataOutputUdpAddress>()).Port, Is.EqualTo(c.EndPoint.Port));
        Assert.That(server.HandlerErrors, Is.Empty);
    }

    [Test]
    public async Task Builtin_OnReceiverState_StillStartsTheAutoStream()
    {
        var runs = 0;
        using var c = new PacketCollector();
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverState>(_ =>
        {
            Interlocked.Increment(ref runs);
            return ControlReply.Builtin();
        }));
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));

        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        Assert.That(runs, Is.EqualTo(1));
    }

    [Test]
    public async Task Builtin_KeepsItsDelayAndCommands()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<MyVendorItem>(_ => ControlReply.Builtin()
            .After(TimeSpan.FromMilliseconds(300))
            .Then(ControlRequest.Set(new RfGain(0, -10)))));
        await using var _ = server;
        await using var __ = client;

        var watch = Stopwatch.StartNew();
        Assert.That((await client.SetAsync(new MyVendorItem(1, 42))).Value, Is.EqualTo(42u));
        Assert.That(watch.ElapsedMilliseconds >= 250, Is.True);
        Assert.That((await client.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-10));
    }

    [Test]
    public async Task RawBuiltin_ReplacesThePayload()
    {
        var (server, client) = await StartAsync(s => s.OnRequest(MyVendorItem.Code, r => r.Type == RequestType.Set
            ? ControlReply.Builtin(Hex.Parse("01 2A 00 00 00"))
            : ControlReply.Builtin()));
        await using var _ = server;
        await using var __ = client;

        var echo = await client.SendAsync(RequestType.Set, MyVendorItem.Code, Hex.Parse("01 07 00 00 00"));
        Assert.That(echo.Payload.ToArray(), Is.EqualTo(Hex.Parse("01 2A 00 00 00")));
        Assert.That((await client.GetAsync<MyVendorItem, byte>(1)).Value, Is.EqualTo(42u));
    }

    [Test]
    public async Task BuiltinWithItemOfAnotherCode_IsNak_AndRecorded()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<RfGain>(_ => ControlReply.Builtin(new ProductId(1))));
        await using var _ = server;
        await using var __ = client;

        await Assert.ThatAsync(() => client.SetAsync(new RfGain(0, -10)), Throws.TypeOf<NetSdrNakException>());
        Assert.That(server.HandlerErrors.Sole(), Is.TypeOf<InvalidOperationException>());
        await Assert.ThatAsync(() => client.GetAsync<RfGain, byte>(0), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task Builtin_FromACommand_RunsTheBuiltinBehaviour()
    {
        const ushort StartCode = 0x8010;
        var (server, client) = await StartAsync(s =>
        {
            s.OnRequest<RfGain>(r => r.Type == RequestType.Set
                ? ControlReply.Builtin(new RfGain(r.Item.Channel, -40))
                : ControlReply.Builtin());
            s.OnRequest(StartCode, _ => ControlReply.Echo.Then(ControlRequest.Set(new RfGain(0, -10))));
        });
        await using var _ = server;
        await using var __ = client;

        await client.SendAsync(RequestType.Set, StartCode, new byte[] { 1 });
        Assert.That(server.HandlerErrors, Is.Empty);
        Assert.That(server.Received.Count(r => r.Code == RfGain.Code), Is.Zero);

        // The Get goes through the handler too, and its Builtin answers it from the state the command left.
        Assert.That((await client.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-40));
    }
}
