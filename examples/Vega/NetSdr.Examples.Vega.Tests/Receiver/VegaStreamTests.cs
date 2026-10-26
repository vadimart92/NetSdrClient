using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using NetSdr.Data;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaStreamTests
{
    [Test]
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
        Assert.That(emulator.Server.Received.Select(r => r.Code).TakeLast(4), Is.EqualTo(new ushort[] { 0x00B8, 0x0020, 0x00C5, 0x0018 }));
        await Eventually.ThatAsync(() => packets.Count >= 3);
        var first = packets.First();
        Assert.That((first.Info.Format, first.Info.Sequence), Is.EqualTo((SampleFormat.Int16, (ushort)0)));
        Assert.That(MemoryMarshal.Cast<byte, short>(first.Samples)[..4].ToArray(), Is.EqualTo(new short[] { 0, -1, 1, -2 }));

        await vega.StopStreamAsync();
        await Task.Delay(100);
        var count = packets.Count;
        await Task.Delay(300);
        Assert.That(packets, Has.Count.EqualTo(count));
    }
}
