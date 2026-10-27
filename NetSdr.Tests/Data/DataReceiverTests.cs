using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Data;
using NetSdr.Testing;

namespace NetSdr.Tests.Data;

public class DataReceiverTests
{
    private static void Send(PacketCollector c, params byte[][] datagrams)
    {
        foreach (var d in datagrams)
            UdpTestSender.Send(c.EndPoint, d);
    }

    private static DataPacketInfo[] Infos(PacketCollector c) => c.Packets.Select(p => p.Info).ToArray();

    [Test]
    public void Bind_Port0_AssignsPort()
    {
        using var c = new PacketCollector();
        Assert.That(c.EndPoint.Address, Is.EqualTo(IPAddress.Loopback));
        Assert.That(c.EndPoint.Port, Is.Not.EqualTo(0));
    }

    [TestCase(1028, SampleFormat.Int16)]
    [TestCase(516, SampleFormat.Int16)]
    [TestCase(1444, SampleFormat.Int24)]
    [TestCase(388, SampleFormat.Int24)]
    [TestCase(104, SampleFormat.Unknown)]
    public async Task Format_FromDatagramLength(int length, SampleFormat format)
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, length));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        var (info, samples) = c.Packets.Single();
        Assert.That(info.Format, Is.EqualTo(format));
        Assert.That(samples, Has.Length.EqualTo(length - 4));
        Assert.That(samples, Is.EqualTo(UdpTestSender.Datagram(0, length)[4..]));
    }

    [Test]
    public async Task Gap_CountsLostAndGapBefore()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(4, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 3);
        Assert.That(Infos(c).Select(i => i.GapBefore), Is.EqualTo(new[] { 0, 0, 2 }));
        Assert.That(Infos(c).Select(i => i.IsCaptureStart), Is.EqualTo(new[] { true, false, false }));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(2));
    }

    [Test]
    public async Task Wrap_FFFF_To_1_IsNotAGap()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0xFFFE, 1028), UdpTestSender.Datagram(0xFFFF, 1028),
            UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 4);
        Assert.That(Infos(c).Select(i => i.GapBefore), Has.All.EqualTo(0));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(0));
    }

    [Test]
    public async Task ZeroMidStream_StartsNewCapture()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(5, 1028), UdpTestSender.Datagram(6, 1028),
            UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 4);
        Assert.That(Infos(c).Select(i => i.IsCaptureStart), Is.EqualTo(new[] { false, false, true, false }));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(0));
    }

    [Test]
    public async Task Reordered_IsNotCountedAsLost()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028),
            UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(3, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 5);
        Assert.That(Infos(c).Select(i => i.GapBefore), Has.All.EqualTo(0));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(0));
    }

    [Test]
    public async Task LateBurst_IsDeliveredWithoutGapAndNotCountedAsLost()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(4, 1028),
            UdpTestSender.Datagram(5, 1028), UdpTestSender.Datagram(2, 1028), UdpTestSender.Datagram(3, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 6);
        Assert.That(Infos(c).Select(i => i.GapBefore), Is.EqualTo(new[] { 0, 0, 2, 0, 0, 0 }));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(2));
    }

    [Test]
    public async Task LargeForwardJump_IsCountedAsLoss()
    {
        using var c = new PacketCollector();
        Send(c, Enumerable.Range(0, 101).Select(i => UdpTestSender.Datagram((ushort)i, 1028)).ToArray());
        Send(c, UdpTestSender.Datagram(40101, 1028), UdpTestSender.Datagram(40102, 1028), UdpTestSender.Datagram(40108, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 104);
        var gaps = Infos(c).Select(i => i.GapBefore).ToArray();
        Assert.That(gaps[101..], Is.EqualTo(new[] { 40000, 0, 5 }));
        Assert.That(gaps[..101], Has.All.EqualTo(0));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(40005));
    }

    [Test]
    public async Task ReorderWindow_EndsAtAThousandPacketsBehind()
    {
        using var c = new PacketCollector();
        // The first packet joins the stream at 5000, so 5001 is expected next. A packet 1023 behind it (3978) is
        // late; one 1024 behind it (3977) is too far back to be told from a jump almost a full cycle ahead.
        Send(c, UdpTestSender.Datagram(5000, 1028), UdpTestSender.Datagram(3978, 1028), UdpTestSender.Datagram(3977, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 3);
        Assert.That(Infos(c).Select(i => i.GapBefore), Is.EqualTo(new[] { 0, 0, 64511 }));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(64511));
    }

    [Test]
    public async Task Rejects_ForeignTypeShortAndWrongLength()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028, type: 5), [0x04, 0x80, 0x00],
            UdpTestSender.Datagram(0, 1000, headerLength: 1028), UdpTestSender.Datagram(0, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        Assert.That(c.Receiver.Statistics.Rejected, Is.EqualTo(3));
    }

    [Test]
    [TestCase(8192)]
    [TestCase(8196)]
    [TestCase(9000)]
    public async Task Jumbo_HeaderZero_DeliveredWithValidation(int length)
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, length));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        Assert.That(c.Packets.Single().Samples, Has.Length.EqualTo(length - 4));
        Assert.That(c.Packets.Single().Info.Format, Is.EqualTo(SampleFormat.Unknown));
        Assert.That(c.Receiver.Statistics.Rejected, Is.Zero);
    }

    [Test]
    public async Task HeaderZero_ShortDatagram_Rejected()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028, headerLength: 8194), UdpTestSender.Datagram(1, 8191, headerLength: 8194),
            UdpTestSender.Datagram(2, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        Assert.That(c.Receiver.Statistics.Rejected, Is.EqualTo(2));
    }

    [Test]
    public async Task RemoteAddress_Filters()
    {
        using var other = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Parse("127.0.0.2") });
        using var same = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Loopback });
        Send(other, UdpTestSender.Datagram(0, 1028));
        Send(same, UdpTestSender.Datagram(0, 1028));
        await Eventually.ThatAsync(() => same.Packets.Count == 1 && other.Receiver.Statistics.Rejected == 1);
        Assert.That(other.Packets, Is.Empty);
    }

    [Test]
    public async Task RemoteAddress_AcceptsEveryPacketFromTheAddress()
    {
        using var c = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Loopback });
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 3);
        Assert.That(c.Receiver.Statistics.Rejected, Is.EqualTo(0));
    }

    [Test]
    public async Task HandlerException_IsCountedAndReceptionContinues()
    {
        var calls = 0;
        using var c = new PacketCollector(onPacket: _ => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException(); });
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => Volatile.Read(ref calls) == 2);
        Assert.That(c.Receiver.Statistics.HandlerErrors, Is.EqualTo(1));
    }

    [Test]
    public async Task Statistics_CountReceivedAndSampleBytes()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => c.Receiver.Statistics.Received == 2);
        Assert.That(c.Receiver.Statistics.Bytes, Is.EqualTo(2048));
    }

    [Test]
    public void SetReceiveBuffer_FromDuration()
    {
        // A small initial buffer, so the assertion below holds only if the call enlarged it.
        using var receiver = new NetSdrDataReceiver(
            (in DataPacketInfo _, ReadOnlySpan<byte> _) => { }, new DataReceiverOptions { InitialReceiveBufferBytes = 8192 });
        Assert.That(receiver.ActualReceiveBufferSize < 200_000, Is.True);
        receiver.SetReceiveBuffer(TimeSpan.FromMilliseconds(200), 1_000_000);
        Assert.That(receiver.ActualReceiveBufferSize >= 200_000, Is.True);
    }

    [Test]
    public async Task Dispose_FromHandler_DoesNotDeadlock()
    {
        var disposed = new TaskCompletionSource();
        using var c = new PacketCollector(onPacket: r => { r.Dispose(); disposed.TrySetResult(); });
        Send(c, UdpTestSender.Datagram(0, 1028));
        await disposed.Task.WaitAsync(Limits.Test);
    }

    [Test]
    public void StartRules()
    {
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        Assert.That(() => receiver.Start(), Throws.TypeOf<InvalidOperationException>());
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();
        Assert.That(() => receiver.Start(), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void BindRules()
    {
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        Assert.That(() => receiver.LocalEndPoint, Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => receiver.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0)), Throws.TypeOf<ArgumentException>());
        receiver.Bind(0);
        Assert.That(receiver.LocalEndPoint.Address, Is.EqualTo(IPAddress.Any));
        Assert.That(receiver.LocalEndPoint.Port, Is.Not.EqualTo(0));
        Assert.That(() => receiver.Bind(0), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Constructor_Rules()
    {
        Assert.That(() => new NetSdrDataReceiver(null!), Throws.TypeOf<ArgumentNullException>());
        Assert.That(
            () => new NetSdrDataReceiver(
                (in DataPacketInfo _, ReadOnlySpan<byte> _) => { },
                new DataReceiverOptions { RemoteAddress = IPAddress.IPv6Loopback }),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Dispose_IsIdempotent_AndEndsUse()
    {
        var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();
        receiver.Dispose();
        receiver.Dispose();
        Assert.That(() => receiver.Start(), Throws.TypeOf<ObjectDisposedException>());
        Assert.That(() => receiver.LocalEndPoint, Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public async Task Dispose_WaitsForTheRunningHandler()
    {
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var c = new PacketCollector(onPacket: _ => { inHandler.Set(); release.Wait(Limits.Test); });
        Send(c, UdpTestSender.Datagram(0, 1028));
        Assert.That(inHandler.Wait(Limits.Test), Is.True);

        var dispose = Task.Run(c.Dispose);
        Assert.That(await Task.WhenAny(dispose, Task.Delay(200)), Is.Not.SameAs(dispose));

        release.Set();
        await dispose.WaitAsync(Limits.Test);
    }

    [Test]
    public Task ReceiveLoop_DoesNotAllocatePerPacket() =>
        AssertNoPerPacketAllocationAsync(new DataReceiverOptions(), time: null);

    // The receive thread's own logging allocates nothing either. Every level is enabled on a logger that itself
    // allocates nothing, and the measured window writes a sequence gap (1204) and a summary that is due (1202), besides
    // the clock read of every 256th datagram.
    [Test]
    public async Task ReceiveLoop_DoesNotAllocatePerPacket_WithLoggingEnabled()
    {
        var (logs, time) = (new CountingLoggerFactory(), new FakeTimeProvider());
        await AssertNoPerPacketAllocationAsync(new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time }, time);
        Assert.That((logs.Count(1204), logs.Count(1202), logs.Count(1201)), Is.EqualTo((2, 2, 0)));   // one gap and one summary per half
    }

    /// <param name="time">Advanced by a whole summary interval before each half, so its 256th datagram logs a summary.</param>
    private static async Task AssertNoPerPacketAllocationAsync(DataReceiverOptions options, FakeTimeProvider? time)
    {
        const int PacketCount = 600;
        const int WarmUp = 300;                                    // the first half; the second half is measured
        var allocated = new long[PacketCount];
        var count = 0;
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) =>
        {
            int i = Volatile.Read(ref count);
            allocated[i] = GC.GetAllocatedBytesForCurrentThread();
            Volatile.Write(ref count, i + 1);
        }, options);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();

        // One sequence number is skipped in each half (before datagrams 100 and 400), so each half has a gap; the
        // summaries fall on datagrams 256 and 512.
        for (int half = 0; half < 2; half++)
        {
            time?.Advance(TimeSpan.FromSeconds(10));
            for (int i = half * WarmUp; i < (half + 1) * WarmUp; i++)
            {
                var sequence = (ushort)(i + (i >= 100 ? 1 : 0) + (i >= 400 ? 1 : 0));
                UdpTestSender.Send(receiver.LocalEndPoint, UdpTestSender.Datagram(sequence, 1028));
            }

            await Eventually.ThatAsync(() => Volatile.Read(ref count) == (half + 1) * WarmUp);
        }

        // The handler itself allocates nothing, so the receive thread must allocate nothing between two calls.
        for (int i = WarmUp; i < PacketCount; i++)
        {
            Assert.That(allocated[i], Is.EqualTo(allocated[i - 1]));
        }
    }

    /// <summary>Loggers enabled at every level that only count the events they get, allocating nothing per entry.</summary>
    private sealed class CountingLoggerFactory : ILoggerFactory
    {
        private readonly int[] _counts = new int[1400];

        public int Count(int eventId) => Volatile.Read(ref _counts[eventId]);

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() { }

        private sealed class Logger(CountingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Interlocked.Increment(ref owner._counts[eventId.Id]);
        }
    }
}
