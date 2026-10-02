using System.Net;
using NetSdr.Control;
using NetSdr.Data;
using NetSdr.Items;
using NetSdr.Tests.Data;
using NetSdr.Testing;

namespace NetSdr.Tests;

public class EndToEndTests
{
    [Fact]
    public async Task TypicalScenario_FromSpec()
    {
        await using var server = new NetSdrTestServer();
        server.OnRequest(TargetName.Code, _ => ControlReply.Bytes("SDR-IP\0"u8.ToArray()));
        await server.StartAsync();
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.Equal("SDR-IP", (await client.GetAsync<TargetName>()).Value);

        using var c = new PacketCollector();
        c.Receiver.SetReceiveBuffer(TimeSpan.FromMilliseconds(200), DataRate.BytesPerSecond(500_000, SampleFormat.Int24));
        await client.SetAsync(new OutputSampleRate(0, 500_000));
        await client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: true));
        await Eventually.ThatAsync(() => c.Packets.Count >= 10);
        await client.SetAsync(ReceiverState.Stop);

        var infos = c.Packets.Take(10).Select(p => p.Info).ToArray();
        Assert.All(infos, i => Assert.Equal(SampleFormat.Int24, i.Format));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (ushort)i), infos.Select(i => i.Sequence));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }
}
