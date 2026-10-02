using System.Net;
using NetSdr.Data;
using NetSdr.Testing;

namespace NetSdr.Tests.Data;

public class DataReceiverTests
{
    static void Send(PacketCollector c, params byte[][] datagrams)
    {
        foreach (var d in datagrams) UdpTestSender.Send(c.EndPoint, d);
    }

    static DataPacketInfo[] Infos(PacketCollector c) => c.Packets.Select(p => p.Info).ToArray();

    [Fact]
    public void Bind_Port0_AssignsPort()
    {
        using var c = new PacketCollector();
        Assert.Equal(IPAddress.Loopback, c.EndPoint.Address);
        Assert.NotEqual(0, c.EndPoint.Port);
    }

    [Theory]
    [InlineData(1028, SampleFormat.Int16)] [InlineData(516, SampleFormat.Int16)]
    [InlineData(1444, SampleFormat.Int24)] [InlineData(388, SampleFormat.Int24)]
    [InlineData(104, SampleFormat.Unknown)]
    public async Task Format_FromDatagramLength(int length, SampleFormat format)
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, length));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        var (info, samples) = c.Packets.Single();
        Assert.Equal(format, info.Format);
        Assert.Equal(length - 4, samples.Length);
        Assert.Equal(UdpTestSender.Datagram(0, length)[4..], samples);
    }

    [Fact]
    public async Task Gap_CountsLostAndGapBefore()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(4, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 3);
        Assert.Equal(new[] { 0, 0, 2 }, Infos(c).Select(i => i.GapBefore));
        Assert.Equal(new[] { true, false, false }, Infos(c).Select(i => i.IsCaptureStart));
        Assert.Equal(2, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Wrap_FFFF_To_1_IsNotAGap()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0xFFFE, 1028), UdpTestSender.Datagram(0xFFFF, 1028),
            UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 4);
        Assert.All(Infos(c), i => Assert.Equal(0, i.GapBefore));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task ZeroMidStream_StartsNewCapture()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(5, 1028), UdpTestSender.Datagram(6, 1028),
            UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 4);
        Assert.Equal(new[] { false, false, true, false }, Infos(c).Select(i => i.IsCaptureStart));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Reordered_IsNotCountedAsLost()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028),
            UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(3, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 5);
        Assert.All(Infos(c), i => Assert.Equal(0, i.GapBefore));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task LateBurst_IsDeliveredWithoutGapAndNotCountedAsLost()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(4, 1028),
            UdpTestSender.Datagram(5, 1028), UdpTestSender.Datagram(2, 1028), UdpTestSender.Datagram(3, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 6);
        Assert.Equal(new[] { 0, 0, 2, 0, 0, 0 }, Infos(c).Select(i => i.GapBefore));
        Assert.Equal(2, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task LargeForwardJump_IsCountedAsLoss()
    {
        using var c = new PacketCollector();
        Send(c, Enumerable.Range(0, 101).Select(i => UdpTestSender.Datagram((ushort)i, 1028)).ToArray());
        Send(c, UdpTestSender.Datagram(40101, 1028), UdpTestSender.Datagram(40102, 1028), UdpTestSender.Datagram(40108, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 104);
        var gaps = Infos(c).Select(i => i.GapBefore).ToArray();
        Assert.Equal(new[] { 40000, 0, 5 }, gaps[101..]);
        Assert.All(gaps[..101], gap => Assert.Equal(0, gap));
        Assert.Equal(40005, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task ReorderWindow_EndsAtAThousandPacketsBehind()
    {
        using var c = new PacketCollector();
        // The first packet joins the stream at 5000, so 5001 is expected next. A packet 1023 behind it (3978) is
        // late; one 1024 behind it (3977) is too far back to be told from a jump almost a full cycle ahead.
        Send(c, UdpTestSender.Datagram(5000, 1028), UdpTestSender.Datagram(3978, 1028), UdpTestSender.Datagram(3977, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 3);
        Assert.Equal(new[] { 0, 0, 64511 }, Infos(c).Select(i => i.GapBefore));
        Assert.Equal(64511, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Rejects_ForeignTypeShortAndWrongLength()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028, type: 5), [0x04, 0x80, 0x00],
            UdpTestSender.Datagram(0, 1000, headerLength: 1028), UdpTestSender.Datagram(0, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        Assert.Equal(3, c.Receiver.Statistics.Rejected);
    }

    [Fact]
    public async Task Jumbo9000_DeliveredOnlyWithoutValidation()
    {
        using var strict = new PacketCollector();
        using var loose = new PacketCollector(new DataReceiverOptions { ValidateLength = false });
        Send(strict, UdpTestSender.Datagram(0, 9000));
        Send(loose, UdpTestSender.Datagram(0, 9000));
        await Eventually.ThatAsync(() => loose.Packets.Count == 1 && strict.Receiver.Statistics.Rejected == 1);
        Assert.Equal(8996, loose.Packets.Single().Samples.Length);
        Assert.Equal(SampleFormat.Unknown, loose.Packets.Single().Info.Format);
    }

    [Fact]
    public async Task RemoteAddress_Filters()
    {
        using var other = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Parse("127.0.0.2") });
        using var same = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Loopback });
        Send(other, UdpTestSender.Datagram(0, 1028));
        Send(same, UdpTestSender.Datagram(0, 1028));
        await Eventually.ThatAsync(() => same.Packets.Count == 1 && other.Receiver.Statistics.Rejected == 1);
        Assert.Empty(other.Packets);
    }

    [Fact]
    public async Task RemoteAddress_AcceptsEveryPacketFromTheAddress()
    {
        using var c = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Loopback });
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 3);
        Assert.Equal(0, c.Receiver.Statistics.Rejected);
    }

    [Fact]
    public async Task HandlerException_IsCountedAndReceptionContinues()
    {
        var calls = 0;
        using var c = new PacketCollector(onPacket: _ => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException(); });
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(1, c.Receiver.Statistics.HandlerErrors);
    }

    [Fact]
    public async Task Statistics_CountReceivedAndSampleBytes()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => c.Receiver.Statistics.Received == 2);
        Assert.Equal(2048, c.Receiver.Statistics.Bytes);
    }

    [Fact]
    public void SetReceiveBuffer_FromDuration()
    {
        // A small initial buffer, so the assertion below holds only if the call enlarged it.
        using var receiver = new NetSdrDataReceiver(
            (in DataPacketInfo _, ReadOnlySpan<byte> _) => { }, new DataReceiverOptions { InitialReceiveBufferBytes = 8192 });
        Assert.True(receiver.ActualReceiveBufferSize < 200_000);
        receiver.SetReceiveBuffer(TimeSpan.FromMilliseconds(200), 1_000_000);
        Assert.True(receiver.ActualReceiveBufferSize >= 200_000);
    }

    [Fact]
    public async Task Dispose_FromHandler_DoesNotDeadlock()
    {
        var disposed = new TaskCompletionSource();
        using var c = new PacketCollector(onPacket: r => { r.Dispose(); disposed.TrySetResult(); });
        Send(c, UdpTestSender.Datagram(0, 1028));
        await disposed.Task.WaitAsync(Limits.Test);
    }

    [Fact]
    public void StartRules()
    {
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        Assert.Throws<InvalidOperationException>(() => receiver.Start());
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();
        Assert.Throws<InvalidOperationException>(() => receiver.Start());
    }

    [Fact]
    public void BindRules()
    {
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        Assert.Throws<InvalidOperationException>(() => receiver.LocalEndPoint);
        Assert.Throws<ArgumentException>(() => receiver.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0)));
        receiver.Bind(0);
        Assert.Equal(IPAddress.Any, receiver.LocalEndPoint.Address);
        Assert.NotEqual(0, receiver.LocalEndPoint.Port);
        Assert.Throws<InvalidOperationException>(() => receiver.Bind(0));
    }

    [Fact]
    public void Constructor_Rules()
    {
        Assert.Throws<ArgumentNullException>(() => new NetSdrDataReceiver(null!));
        Assert.Throws<ArgumentException>(() => new NetSdrDataReceiver(
            (in DataPacketInfo _, ReadOnlySpan<byte> _) => { },
            new DataReceiverOptions { RemoteAddress = IPAddress.IPv6Loopback }));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndEndsUse()
    {
        var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();
        receiver.Dispose();
        receiver.Dispose();
        Assert.Throws<ObjectDisposedException>(() => receiver.Start());
        Assert.Throws<ObjectDisposedException>(() => receiver.LocalEndPoint);
    }

    [Fact]
    public async Task Dispose_WaitsForTheRunningHandler()
    {
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var c = new PacketCollector(onPacket: _ => { inHandler.Set(); release.Wait(Limits.Test); });
        Send(c, UdpTestSender.Datagram(0, 1028));
        Assert.True(inHandler.Wait(Limits.Test));

        var dispose = Task.Run(c.Dispose);
        Assert.NotSame(dispose, await Task.WhenAny(dispose, Task.Delay(200)));

        release.Set();
        await dispose.WaitAsync(Limits.Test);
    }

    [Fact]
    public async Task ReceiveLoop_DoesNotAllocatePerPacket()
    {
        const int PacketCount = 300;
        const int WarmUp = 100;
        var allocated = new long[PacketCount];
        var count = 0;
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) =>
        {
            int i = Volatile.Read(ref count);
            allocated[i] = GC.GetAllocatedBytesForCurrentThread();
            Volatile.Write(ref count, i + 1);
        });
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();

        for (ushort sequence = 0; sequence < PacketCount; sequence++)
        {
            UdpTestSender.Send(receiver.LocalEndPoint, UdpTestSender.Datagram(sequence, 1028));
        }

        await Eventually.ThatAsync(() => Volatile.Read(ref count) == PacketCount);
        // The handler itself allocates nothing, so the receive thread must allocate nothing between two calls.
        for (int i = WarmUp; i < PacketCount; i++)
        {
            Assert.Equal(allocated[i - 1], allocated[i]);
        }
    }
}
