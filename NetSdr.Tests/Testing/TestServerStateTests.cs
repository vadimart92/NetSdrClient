using System.Net;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;
using static NetSdr.Tests.Loopback;

namespace NetSdr.Tests.Testing;

public class TestServerStateTests
{
    [Test]
    public async Task TryReadLatest_ReturnsTheNewestSet_WhateverTheKey()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(new RfGain(0, -10));
        await client.SetAsync(new RfGain(1, -20));

        Assert.That(server.TryReadLatest(out RfGain gain), Is.True);
        Assert.That((gain.Channel, gain.GainDb), Is.EqualTo(((byte)1, (sbyte)-20)));
    }

    [Test]
    public async Task TryReadLatest_WithKey_ReturnsTheNewestOfThatKey()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(new RfGain(0, -10));
        await client.SetAsync(new RfGain(1, -20));
        await client.SetAsync(new RfGain(0, -30));

        Assert.That(server.TryReadLatest((byte)0, out RfGain first), Is.True);
        Assert.That(first.GainDb, Is.EqualTo(-30));
        Assert.That(server.TryReadLatest((byte)1, out RfGain second), Is.True);
        Assert.That(second.GainDb, Is.EqualTo(-20));
        Assert.That(server.TryReadLatest((byte)2, out RfGain _), Is.False);
    }

    [Test]
    public void TryReadLatest_IsFalse_WhenNothingIsStored_AndSeesPreloadAndClearState()
    {
        var server = new NetSdrTestServer();
        Assert.That(server.TryReadLatest(out ProductId _), Is.False);

        server.Preload(new ProductId(0x03524453));
        Assert.That(server.TryReadLatest(out ProductId product), Is.True);
        Assert.That(product.Value, Is.EqualTo(0x03524453u));

        server.ClearState();
        Assert.That(server.TryReadLatest(out ProductId _), Is.False);
    }

    [Test]
    public void TryReadLatest_ReadsVariableItems()
    {
        var server = new NetSdrTestServer();
        server.Preload(new TargetName("NetSDR"));

        Assert.That(server.TryReadLatest(out TargetName name), Is.True);
        Assert.That(name.Value, Is.EqualTo("NetSDR"));
    }

    [Test]
    public async Task TryReadLatest_SeesWhatABuiltinReplyStored()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<DataOutputUdpAddress>(r => r.Type == RequestType.Set
            ? ControlReply.Builtin(new DataOutputUdpAddress(r.Item.Ip, 50_000))
            : ControlReply.Builtin()));
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(DataOutputUdpAddress.For(new IPEndPoint(IPAddress.Loopback, 40_000)));

        Assert.That(server.TryReadLatest(out DataOutputUdpAddress address), Is.True);
        Assert.That(address.Port, Is.EqualTo(50_000));
    }

    [Test]
    public async Task TryReadLatest_ThrowsForAPayloadThatIsNotTheItem()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await client.SendAsync(RequestType.Set, OutputSampleRate.Code, new byte[] { 1 });

        Assert.That(() => server.TryReadLatest(out OutputSampleRate _), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task Run_AfterASettingTooShortToRead_IsNak_AndRecorded()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await using var __ = client;
        await client.SendAsync(RequestType.Set, OutputSampleRate.Code, new byte[] { 1 });

        await Assert.ThatAsync(
            () => client.SetAsync(ReceiverState.Start(complex: true, bits24: false)),
            Throws.TypeOf<NetSdrNakException>());
        Assert.That(server.HandlerErrors.Sole(), Is.TypeOf<ArgumentOutOfRangeException>());
    }
}
