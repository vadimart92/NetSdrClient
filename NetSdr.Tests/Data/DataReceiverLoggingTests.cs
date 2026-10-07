using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Data;
using NetSdr.Testing;

namespace NetSdr.Tests.Data;

public class DataReceiverLoggingTests
{
    const string ReceiveThread = "NetSdr data receiver";

    static void Send(IPEndPoint target, ushort first, int count)
    {
        for (int i = 0; i < count; i++) UdpTestSender.Send(target, UdpTestSender.Datagram((ushort)(first + i), 1028));
    }

    static Task ReceivedAsync(NetSdrDataReceiver receiver, long count) =>
        Eventually.ThatAsync(() => receiver.Statistics.Received == count);

    static void Ignore(in DataPacketInfo info, ReadOnlySpan<byte> samples) { }

    [Fact]
    public async Task Summary_CleanInterval_Debug1201()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        Send(c.EndPoint, 1, 256);
        await ReceivedAsync(c.Receiver, 256);
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 257, 256);
        await Eventually.ThatAsync(() => logs.Events(1201).Count == 1);
        Assert.Equal(("512", LogLevel.Debug), (logs.Events(1201)[0].Value("Received"), logs.Events(1201)[0].Level));
        Assert.Empty(logs.Events(1202));
    }

    [Fact]
    public async Task Summary_LossInInterval_Warning1202()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        Send(c.EndPoint, 1, 100);
        Send(c.EndPoint, 105, 156);                            // 4 lost before 105
        await ReceivedAsync(c.Receiver, 256);
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 261, 256);
        await Eventually.ThatAsync(() => logs.Events(1202).Count == 1);
        var loss = logs.Events(1202)[0];
        Assert.Equal(("4", "512", LogLevel.Warning), (loss.Value("Lost"), loss.Value("Received"), loss.Level));
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 517, 256);
        await Eventually.ThatAsync(() => logs.Events(1201).Count == 1);
        Assert.Equal("256", logs.Events(1201)[0].Value("Received"));
    }

    [Fact]
    public async Task Summary_Rejected_Warning1202()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        Send(c.EndPoint, 1, 200);
        for (int i = 0; i < 56; i++) UdpTestSender.Send(c.EndPoint, UdpTestSender.Datagram(1, 1028, type: 5));
        await Eventually.ThatAsync(() => c.Receiver.Statistics is { Received: 200, Rejected: 56 });
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 201, 256);
        await Eventually.ThatAsync(() => logs.Events(1202).Count == 1);
        Assert.Equal("56", logs.Events(1202)[0].Value("Rejected"));
    }

    [Fact]
    public async Task Summary_ClockReadOncePer256Datagrams()
    {
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        var c = new PacketCollector(new DataReceiverOptions { TimeProvider = clock });
        Send(c.EndPoint, 1, 1024);
        await ReceivedAsync(c.Receiver, 1024);
        c.Dispose();                                           // joins the receive thread: every read is done
        Assert.Equal(4, clock.TimestampReaders.Count(n => n == ReceiveThread));
        Assert.Equal(6, clock.TimestampReaders.Count);          // plus one in Start and one in Dispose
    }

    [Fact]
    public async Task Summary_Disabled_NoClockReadsOnReceiveThread()
    {
        var logs = new FakeLoggerFactory();
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        var c = new PacketCollector(new DataReceiverOptions
        {
            LoggerFactory = logs, TimeProvider = clock, StatisticsLogInterval = Timeout.InfiniteTimeSpan,
        });
        Send(c.EndPoint, 1, 1024);
        await ReceivedAsync(c.Receiver, 1024);
        c.Dispose();
        Assert.DoesNotContain(ReceiveThread, clock.TimestampReaders);
        Assert.Empty(logs.Events(1201).Concat(logs.Events(1202)));
    }

    [Fact]
    public async Task HandlerErrors_FirstPerIntervalLogged()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var receiver = new NetSdrDataReceiver(
            (in DataPacketInfo _, ReadOnlySpan<byte> _) => throw new InvalidOperationException("handler"),
            new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();
        Send(receiver.LocalEndPoint, 1, 256);
        await Eventually.ThatAsync(() => receiver.Statistics.HandlerErrors == 256);
        Assert.IsType<InvalidOperationException>(Assert.Single(logs.Events(1205)).Exception);
        time.Advance(TimeSpan.FromSeconds(10));
        Send(receiver.LocalEndPoint, 257, 256);
        await Eventually.ThatAsync(() => logs.Events(1202).Count == 1);
        Assert.Equal("512", logs.Events(1202)[0].Value("HandlerErrors"));
        Send(receiver.LocalEndPoint, 513, 1);                  // the next interval logs its first error again
        await Eventually.ThatAsync(() => logs.Events(1205).Count == 2);
    }

    [Fact]
    public async Task SequenceGap_DebugPerGap()
    {
        var logs = new FakeLoggerFactory();
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        Send(c.EndPoint, 1, 10);
        Send(c.EndPoint, 13, 8);                               // 2 lost
        Send(c.EndPoint, 25, 6);                               // 4 lost
        await ReceivedAsync(c.Receiver, 24);
        Assert.Equal(new (string?, string?)[] { ("2", "13"), ("4", "25") }, logs.Events(1204).Select(r => (r.Value("Gap"), r.Value("Sequence"))));
        Assert.All(logs.Events(1204), r => Assert.Equal(LogLevel.Debug, r.Level));
    }

    [Fact]
    public async Task NoPerPacketEntries_EvenAtTrace()
    {
        var logs = new FakeLoggerFactory(LogLevel.Trace);
        var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        Send(c.EndPoint, 1, 1000);
        await ReceivedAsync(c.Receiver, 1000);
        c.Dispose();
        var ids = logs.Collector.GetSnapshot().Select(r => r.Id.Id).ToArray();
        Assert.All(ids, id => Assert.Contains(id, new[] { 1200, 1201, 1202, 1203 }));
        Assert.Equal((1, 1), (ids.Count(id => id == 1200), ids.Count(id => id == 1203)));
    }

    [Fact]
    public async Task StartAndDispose_Information()
    {
        var logs = new FakeLoggerFactory();
        var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        var started = Assert.Single(logs.Events(1200));
        Assert.Equal((LogLevel.Information, c.EndPoint.ToString()), (started.Level, started.Value("LocalEndPoint")));
        Send(c.EndPoint, 1, 3);
        await ReceivedAsync(c.Receiver, 3);
        c.Dispose();
        var stopped = Assert.Single(logs.Events(1203));
        Assert.Equal(("3", LogLevel.Information), (stopped.Value("Received"), stopped.Level));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-10_000_000L)]                                 // -1 s
    [InlineData((int.MaxValue + 1L) * TimeSpan.TicksPerMillisecond)]
    public void StatisticsLogInterval_Invalid_Throws(long ticks) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NetSdrDataReceiver(Ignore, new DataReceiverOptions { StatisticsLogInterval = TimeSpan.FromTicks(ticks) }));

    [Fact]
    public void StatisticsLogInterval_Infinite_Accepted() =>
        new NetSdrDataReceiver(Ignore, new DataReceiverOptions { StatisticsLogInterval = Timeout.InfiniteTimeSpan }).Dispose();

    // Review Focus 5.
    [Fact]
    public void Dispose_Twice_StoppedLoggedOnce_NeverStarted_Silent()
    {
        var logs = new FakeLoggerFactory();
        var started = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        started.Dispose();
        started.Dispose();
        Assert.Single(logs.Events(1203));

        var clock = new CountingTimeProvider(TimeProvider.System);
        var idle = new NetSdrDataReceiver(Ignore, new DataReceiverOptions { LoggerFactory = logs, TimeProvider = clock });
        idle.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        idle.Dispose();
        idle.Dispose();
        Assert.Single(logs.Events(1203));
        Assert.Empty(clock.TimestampReaders);
    }
}
