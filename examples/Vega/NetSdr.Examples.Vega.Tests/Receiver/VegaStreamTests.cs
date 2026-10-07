using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using NetSdr.Data;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaStreamTests
{
    [Fact]
    public async Task StartStream_DeliversCounterData_StopEndsIt()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        var packets = new ConcurrentQueue<(DataPacketInfo Info, byte[] Samples)>();
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo info, ReadOnlySpan<byte> samples) =>
            packets.Enqueue((info, samples.ToArray())));
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();

        await vega.StartStreamAsync(receiver.LocalEndPoint, 7_100_000, 200_000);
        Assert.Equal(new ushort[] { 0x00B8, 0x0020, 0x00C5, 0x0018 },
            emulator.Server.Received.Select(r => r.Code).TakeLast(4));
        await Eventually.ThatAsync(() => packets.Count >= 3);
        var first = packets.First();
        Assert.Equal((SampleFormat.Int16, (ushort)0), (first.Info.Format, first.Info.Sequence));
        Assert.Equal(new short[] { 0, -1, 1, -2 }, MemoryMarshal.Cast<byte, short>(first.Samples)[..4].ToArray());

        await vega.StopStreamAsync();
        await Task.Delay(100);
        var count = packets.Count;
        await Task.Delay(300);
        Assert.Equal(count, packets.Count);
    }
}
