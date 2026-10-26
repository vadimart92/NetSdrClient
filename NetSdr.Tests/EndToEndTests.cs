using System.Net;
using NetSdr.Control;
using NetSdr.Data;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Control;
using NetSdr.Tests.Data;

namespace NetSdr.Tests;

public class EndToEndTests
{
    [Test]
    public async Task TypicalScenario_FromSpec()
    {
        await using var server = new NetSdrTestServer();
        server.OnRequest(TargetName.Code, _ => ControlReply.Bytes("SDR-IP\0"u8.ToArray()));
        await server.StartAsync();
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.That((await client.GetAsync<TargetName>()).Value, Is.EqualTo("SDR-IP"));

        using var c = new PacketCollector();
        c.Receiver.SetReceiveBuffer(TimeSpan.FromMilliseconds(200), DataRate.BytesPerSecond(500_000, SampleFormat.Int24));
        await client.SetAsync(new OutputSampleRate(0, 500_000));
        await client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: true));
        await Eventually.ThatAsync(() => c.Packets.Count >= 10);
        await client.SetAsync(ReceiverState.Stop);

        var infos = c.Packets.Take(10).Select(p => p.Info).ToArray();
        Assert.That(infos.Select(i => i.Format), Has.All.EqualTo(SampleFormat.Int24));
        Assert.That(infos.Select(i => i.Sequence), Is.EqualTo(Enumerable.Range(0, 10).Select(i => (ushort)i)));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(0));
    }

    [Test]
    public async Task EndToEnd_StreamResumesAfterReconnect()
    {
        using var c = new PacketCollector();
        var logs = new FakeLoggerFactory();
        var options = Resilient.Fast(logs);
        options.ConnectionRestored = async (ctx, ct) =>
        {
            await ctx.Client.SetAsync(DataOutputUdpAddress.For(new IPEndPoint(ctx.Client.LocalEndPoint!.Address, c.EndPoint.Port)), ct);
            await ctx.Client.SetAsync(ReceiverState.Start(complex: true, bits24: false), ct);
        };
        var (server, client) = await Resilient.StartAsync(options);
        await using (server)
        await using (client)
        {
            await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
            await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
            await Eventually.ThatAsync(() => c.Packets.Count >= 10);
            await server.DisconnectClientAsync();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            await Eventually.ThatAsync(() => c.Packets.Count(p => p.Info.IsCaptureStart) >= 2);
            Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(0));
            Assert.That(c.Receiver.Statistics.HandlerErrors, Is.EqualTo(0));
        }
    }
}
