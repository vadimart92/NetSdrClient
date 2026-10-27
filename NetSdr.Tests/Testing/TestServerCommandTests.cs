using System.Collections.Concurrent;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Data;
using NetSdr.Tests.Items;
using static NetSdr.Tests.Loopback;

namespace NetSdr.Tests.Testing;

public class TestServerCommandTests
{
    private const ushort StartCode = 0x8010;
    private const ushort StopCode = 0x8011;

    [Test]
    public async Task VendorCommand_StartsAndStopsTheStandardStream()
    {
        var (server, client) = await StartAsync(s =>
        {
            s.OnRequest(StartCode, _ => ControlReply.Echo.Then(ControlRequest.Set(ReceiverState.Start(complex: true, bits24: false))));
            s.OnRequest(StopCode, _ => ControlReply.Echo.Then(ControlRequest.Set(ReceiverState.Stop)));
        });
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));

        await client.SendAsync(RequestType.Set, StartCode, new byte[] { 1 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        Assert.That((await client.GetAsync<ReceiverState>()).IsRunning, Is.True);

        await client.SendAsync(RequestType.Set, StopCode, new byte[] { 1 });
        await Task.Delay(100);
        var count = c.Packets.Count;
        await Task.Delay(300);
        Assert.That(c.Packets, Has.Count.EqualTo(count));

        // Only what the client sent is recorded; the commands the server ran itself are not.
        Assert.That(
            server.Received.Where(r => r.Type == RequestType.Set).Select(r => r.Code),
            Is.EqualTo(new[] { DataOutputUdpAddress.Code, StartCode, StopCode }));
        Assert.That(server.HandlerErrors, Is.Empty);
    }

    [Test]
    public async Task Commands_RunInOrder_ThroughHandlersAndState_BeforeTheReply()
    {
        var seen = new ConcurrentQueue<uint>();
        var (server, client) = await StartAsync(s =>
        {
            s.OnRequest<MyVendorItem>(r =>
            {
                seen.Enqueue(r.Item.Value);
                return ControlReply.Echo;
            });
            s.OnRequest(StartCode, _ =>
            {
                // The reply the client sees is chosen before the commands run.
                seen.Enqueue(0);
                return ControlReply.Echo
                    .Then(ControlRequest.Set(new MyVendorItem(0, 1)))
                    .Then(ControlRequest.Set(new MyVendorItem(0, 2)), ControlRequest.Set(new RfGain(0, -10)));
            });
        });
        await using var _ = server;
        await using var __ = client;

        await client.SendAsync(RequestType.Set, StartCode, new byte[] { 1 });
        Assert.That(seen, Is.EqualTo(new uint[] { 0, 1, 2 }));
        Assert.That((await client.GetAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-10));
    }

    [Test]
    public async Task FailingCommand_TurnsTheReplyIntoNak_AndIsRecorded()
    {
        var failure = new InvalidOperationException("command failure");
        var (server, client) = await StartAsync(s =>
        {
            s.OnRequest<MyVendorItem>(_ => throw failure);
            s.OnRequest(StartCode, _ => ControlReply.Echo.Then(ControlRequest.Set(new MyVendorItem(0, 1))));
        });
        await using var _ = server;
        await using var __ = client;

        await Assert.ThatAsync(
            () => client.SendAsync(RequestType.Set, StartCode, new byte[] { 1 }),
            Throws.TypeOf<NetSdrNakException>());
        Assert.That(server.HandlerErrors.Sole(), Is.SameAs(failure));
    }

    [Test]
    public async Task CommandAnsweredWithNak_LeavesTheReplyAlone()
    {
        var (server, client) = await StartAsync(s =>
        {
            s.OnRequest<MyVendorItem>(_ => ControlReply.Nak);
            s.OnRequest(StartCode, _ => ControlReply.Echo.Then(ControlRequest.Set(new MyVendorItem(0, 1))));
        });
        await using var _ = server;
        await using var __ = client;

        var reply = await client.SendAsync(RequestType.Set, StartCode, new byte[] { 7 });
        Assert.That(reply.Payload.ToArray(), Is.EqualTo(new byte[] { 7 }));
        Assert.That(server.HandlerErrors, Is.Empty);
    }

    [Test]
    public async Task CommandThatChainsItself_EndsWithNak()
    {
        var calls = 0;
        var (server, client) = await StartAsync(s => s.OnRequest(StartCode, request =>
        {
            Interlocked.Increment(ref calls);
            return ControlReply.Echo.Then(ControlRequest.Create(RequestType.Set, StartCode, request.Payload));
        }));
        await using var _ = server;
        await using var __ = client;

        await Assert.ThatAsync(
            () => client.SendAsync(RequestType.Set, StartCode, new byte[] { 1 }),
            Throws.TypeOf<NetSdrNakException>());
        Assert.That(server.HandlerErrors.Sole(), Is.TypeOf<InvalidOperationException>());
        Assert.That(calls, Is.EqualTo(9));    // the request and 8 levels of commands
    }

    [Test]
    public async Task SilentReply_StillRunsItsCommands()
    {
        var (server, client) = await StartAsync(s =>
            s.OnRequest(StartCode, _ => ControlReply.Silent.Then(ControlRequest.Set(ReceiverState.Start(complex: true, bits24: false)))));
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));

        // No reply comes, so the request is left to fail when the client is disposed.
        Task unanswered = client.SendAsync(RequestType.Set, StartCode, new byte[] { 1 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        Assert.That(unanswered.IsCompleted, Is.False);
    }

    [Test]
    public void Create_RejectsTypesThatAreNotControlRequests()
    {
        Assert.That(() => ControlRequest.Create(RequestType.DataAck, 0x0018, ReadOnlyMemory<byte>.Empty),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        var get = ControlRequest.Create(RequestType.Get, 0x0020, new byte[] { 2 });
        Assert.That((get.Type, get.Code, get.Payload.ToArray()), Is.EqualTo((RequestType.Get, (ushort)0x0020, new byte[] { 2 })));
    }
}
