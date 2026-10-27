using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetSdr.Data;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Data;

namespace NetSdr.Tests.Testing;

public class TestServerStreamingTests
{
    private static short[] Shorts(byte[] samples) => MemoryMarshal.Cast<byte, short>(samples).ToArray();

    [Test]
    public async Task ManualStream_Int16_CounterData()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 256_000 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        var packets = c.Packets.ToArray();
        Assert.That(
            (packets[0].Info.Format, packets[0].Info.Sequence, packets[0].Info.IsCaptureStart),
            Is.EqualTo((SampleFormat.Int16, (ushort)0, true)));
        Assert.That(packets[0].Samples, Has.Length.EqualTo(1024));
        var first = Shorts(packets[0].Samples);
        Assert.That((first[0], first[1], first[510], first[511]), Is.EqualTo((0, -1, 255, -256)));
        Assert.That(Shorts(packets[1].Samples)[0], Is.EqualTo(256));
    }

    [Test]
    public async Task ManualStream_Int24_CounterData()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { Format = SampleFormat.Int24, SampleRate = 240_000 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        var second = c.Packets.ElementAt(1);
        Assert.That((second.Info.Format, second.Samples.Length), Is.EqualTo((SampleFormat.Int24, 1440)));
        Assert.That(second.Samples[..6], Is.EqualTo(Hex.Parse("F0 00 00 0F FF FF")));   // sample 240
    }

    [Test]
    public async Task DropPacket_ProducesGap()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint,
            new StreamOptions { SampleRate = 256_000, DropPacket = seq => seq is 3 or 4 });
        await Eventually.ThatAsync(() => c.Packets.Any(p => p.Info.Sequence == 5));
        await server.StopStreamingAsync();
        var five = c.Packets.Single(p => p.Info.Sequence == 5);
        Assert.That(five.Info.GapBefore, Is.EqualTo(2));
        Assert.That(Shorts(five.Samples)[0], Is.EqualTo(5 * 256));
        Assert.That(c.Receiver.Statistics.Lost, Is.EqualTo(2));
    }

    [Test]
    [TestCase(8192)]
    [TestCase(8996)]
    public async Task Jumbo_DeliveredWithValidation(int payloadSize)
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = payloadSize, SampleRate = 22_490 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await server.StopStreamingAsync();
        Assert.That(c.Packets.First().Samples, Has.Length.EqualTo(payloadSize));
        Assert.That(c.Receiver.Statistics.Rejected, Is.Zero);
    }

    [TestCase(false, DataOutputPacketSize.Large, 1028)]
    [TestCase(false, DataOutputPacketSize.Small, 516)]
    [TestCase(true, DataOutputPacketSize.Large, 1444)]
    [TestCase(true, DataOutputPacketSize.Small, 388)]
    public async Task AutoStream_PacketSizeFromState(bool bits24, byte size, int datagramLength)
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new DataOutputPacketSize(size));
        await client.SetAsync(new OutputSampleRate(0, 100_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        Assert.That(c.Packets.First().Samples, Has.Length.EqualTo(datagramLength - 4));
        Assert.That(c.Packets.First().Info.Format, Is.EqualTo(bits24 ? SampleFormat.Int24 : SampleFormat.Int16));
    }

    [Test]
    public async Task AutoStream_StopEndsStream()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await client.SetAsync(ReceiverState.Stop);
        await Task.Delay(100);
        var count = c.Packets.Count;
        await Task.Delay(300);
        Assert.That(c.Packets, Has.Count.EqualTo(count));
    }

    [Test]
    public async Task AutoStream_ZeroIp_UsesClientAddress()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new DataOutputUdpAddress(0, (ushort)c.EndPoint.Port));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
    }

    [Test]
    public async Task AutoStream_ExplicitOptionsWin()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Stream.PayloadSize = 200);
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        Assert.That(c.Packets.First().Samples, Has.Length.EqualTo(200));
    }

    [Test]
    public async Task AutoStream_Disabled_NoData()
    {
        var (server, client) = await Loopback.StartAsync(s => s.AutoStream = false);
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Task.Delay(300);
        Assert.That(c.Packets, Is.Empty);
    }

    [Test]
    public async Task RealTimePacing_LimitsRate()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 2_560 });   // 10 packets/s
        await Task.Delay(500);
        await server.StopStreamingAsync();
        Assert.That(c.Packets.Count, Is.InRange(2, 10));
    }

    // The tests below go beyond the brief: the carried client-left and dispose rules, and the rules the brief
    // states but its tests do not exercise.

    [Test]
    public async Task StreamOptions_And_Server_Defaults()
    {
        await using var server = new NetSdrTestServer();
        Assert.That(server.AutoStream, Is.True);
        var options = new StreamOptions();
        Assert.That(options.Format, Is.Null);
        Assert.That(options.PayloadSize, Is.Null);
        Assert.That(options.SampleRate, Is.Null);
        Assert.That(options.DropPacket, Is.Null);
        Assert.That((options.Channels, options.Pacing), Is.EqualTo((1, Pacing.RealTime)));
        var b = new byte[4];
        options.Source(b, 5, SampleFormat.Int16);
        Assert.That(b, Is.EqualTo(Hex.Parse("05 00 FA FF")));
    }

    [Test]
    public async Task Unthrottled_SendsWithoutWaitingForTheSampleRate()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        // At this rate RealTime would need ten seconds for a packet.
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 100, Pacing = Pacing.Unthrottled });
        await Eventually.ThatAsync(() => c.Packets.Count >= 20);
        await server.StopStreamingAsync();
    }

    [Test]
    public async Task RealTimePacing_ChannelsShareTheSampleRate()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        // 256 samples a packet over two channels at 2560 Hz: 50 ms a packet, where one channel would need 100 ms.
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 2_560, Channels = 2 });
        await Eventually.ThatAsync(() => c.ArrivalTimestamps.Count >= 10);
        await server.StopStreamingAsync();
        var arrivals = c.ArrivalTimestamps.ToArray();
        var elapsed = Stopwatch.GetElapsedTime(arrivals[0], arrivals[9]);
        Assert.That(elapsed.TotalMilliseconds, Is.InRange(300, 700));   // 450 ms; 900 ms for one channel
    }

    [Test]
    public async Task ManualStream_WithoutOptions_UsesServerStreamOptions()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        server.Stream.PayloadSize = 200;
        server.Stream.Format = SampleFormat.Int24;
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint);
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await server.StopStreamingAsync();
        // 200 bytes hold 33 whole 24-bit samples (198 bytes) and 2 bytes of zeros.
        Assert.That(c.Packets.First().Samples, Has.Length.EqualTo(200));
        Assert.That(c.Packets.First().Samples[198..], Is.EqualTo(new byte[] { 0, 0 }));
    }

    [Test]
    public async Task ManualStream_PayloadRemainder_IsZero()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = 10 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await server.StopStreamingAsync();
        Assert.That(c.Packets.First().Samples, Is.EqualTo(Hex.Parse("00 00 FF FF 01 00 FE FF 00 00")));
    }

    [Test]
    public async Task ManualStream_FromBufferSource_ContinuesAcrossPackets()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            PayloadSize = 12,    // 3 samples
            SampleRate = 3_000,
            Source = SampleSources.FromBuffer(Hex.Parse("01 02 03 04 05 06 07 08")),
        });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        Assert.That(c.Packets.ElementAt(0).Samples, Is.EqualTo(Hex.Parse("01 02 03 04 05 06 07 08 01 02 03 04")));
        Assert.That(c.Packets.ElementAt(1).Samples, Is.EqualTo(Hex.Parse("05 06 07 08 01 02 03 04 05 06 07 08")));
    }

    [Test]
    public async Task ManualStream_Packets_PlayInOrderAndOverAgain()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            SampleRate = 4_000,
            Packets = [Hex.Parse("01 02 03 04"), Hex.Parse("05 06 07 08 09 0A 0B 0C")],
        });
        await Eventually.ThatAsync(() => c.Packets.Count >= 5);
        await server.StopStreamingAsync();
        var packets = c.Packets.Take(5).ToArray();
        Assert.That(packets.Select(p => p.Info.Sequence), Is.EqualTo(new ushort[] { 0, 1, 2, 3, 4 }));
        Assert.That(packets.Select(p => p.Samples.Length), Is.EqualTo(new[] { 4, 8, 4, 8, 4 }));
        Assert.That(packets[2].Samples, Is.EqualTo(Hex.Parse("01 02 03 04")));
        Assert.That(packets[3].Samples, Is.EqualTo(Hex.Parse("05 06 07 08 09 0A 0B 0C")));
        Assert.That(c.Receiver.Statistics.Rejected, Is.Zero);
    }

    [Test]
    public async Task ManualStream_PacketsWithTimestamps_StampTheFirstSampleOfEach()
    {
        // Sample 0 is 999 ms into a second: at 1 kHz that is tick 999, and the next sample starts the next second.
        var second = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            SampleRate = 1_000,
            Packets = [Hex.Parse("01 02 03 04"), Hex.Parse("05 06 07 08 09 0A 0B 0C")],
            Timestamps = true,
            TimestampOrigin = second.AddMilliseconds(999),
        });
        await Eventually.ThatAsync(() => c.Packets.Count >= 4);
        await server.StopStreamingAsync();
        var packets = c.Packets.Take(4).ToArray();
        Assert.That(packets.Select(p => p.Samples.Length), Is.EqualTo(new[] { 12, 16, 12, 16 }));
        Assert.That(packets[1].Samples[..8], Is.EqualTo(Hex.Parse("05 06 07 08 09 0A 0B 0C")));
        Assert.That(
            packets.Select(p => PacketTimestamp.ReadTrailer(p.Samples)),
            Is.EqualTo(new[]
            {
                new PacketTimestamp(999, 1_800_000_000),
                new PacketTimestamp(0, 1_800_000_001),
                new PacketTimestamp(2, 1_800_000_001),
                new PacketTimestamp(3, 1_800_000_001),
            }));
    }

    [TestCase(SampleFormat.Int16, 2046)]
    [TestCase(SampleFormat.Int24, 1364)]
    public async Task ManualStream_Jumbo8192WithTimestamps_TimestampTakesTheLastSamples(SampleFormat format, int samplesPerPacket)
    {
        var origin = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            Format = format,
            PayloadSize = 8192,
            SampleRate = 1_000_000,
            Timestamps = true,
            TimestampOrigin = origin,
        });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        var second = c.Packets.ElementAt(1).Samples;
        int sampleBytes = format == SampleFormat.Int16 ? 4 : 6;
        Assert.That(second, Has.Length.EqualTo(8192));
        Assert.That(PacketTimestamp.ReadTrailer(second), Is.EqualTo(new PacketTimestamp((uint)samplesPerPacket, 1_800_000_000)));

        // 8184 bytes are whole samples in both formats: the last sample ends right where the timestamp starts.
        Assert.That(samplesPerPacket * sampleBytes, Is.EqualTo(8192 - PacketTimestamp.Size));
        Assert.That(second[((samplesPerPacket - 1) * sampleBytes)..][..2],
            Is.EqualTo(BitConverter.GetBytes((short)(2 * samplesPerPacket - 1))));
        Assert.That(c.Receiver.Statistics.Rejected, Is.Zero);
    }

    [Test]
    public async Task ManualStream_Timestamps_CountSamplesPerChannel()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            PayloadSize = 16 + PacketTimestamp.Size,    // 4 samples, 2 per channel
            SampleRate = 1_000,
            Channels = 2,
            Timestamps = true,
            TimestampOrigin = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
        });
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await server.StopStreamingAsync();
        Assert.That(
            c.Packets.Take(3).Select(p => PacketTimestamp.ReadTrailer(p.Samples).Ticks),
            Is.EqualTo(new uint[] { 0, 2, 4 }));
    }

    [Test]
    public async Task ManualStream_TimestampsWithoutOrigin_FollowTheClock()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        var before = DateTimeOffset.UtcNow;
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 256_000, Timestamps = true });
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await server.StopStreamingAsync();
        var after = DateTimeOffset.UtcNow;
        var first = PacketTimestamp.ReadTrailer(c.Packets.First().Samples).ToDateTimeOffset(new SampleRateTimeScale(256_000));
        Assert.That(first, Is.InRange(before.AddMilliseconds(-1), after));
    }

    [TestCase(SampleFormat.Int24, null, 1444, "A4 85 00 00")]     // the spec's own example
    [TestCase(SampleFormat.Int16, 8187, 8191, "FF 9F 00 00")]     // the longest length the field holds
    [TestCase(SampleFormat.Int16, 8190, 8194, "00 80 00 00")]     // 8194 is the standard encoding of 0
    [TestCase(SampleFormat.Int16, 8996, 9000, "00 80 00 00")]     // a jumbo packet is written as 0 as well
    public async Task Datagram_HeaderAndSequence(SampleFormat format, int? payloadSize, int datagramLength, string headerHex)
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await server.StartStreamingAsync((IPEndPoint)udp.LocalEndPoint!,
            new StreamOptions { Format = format, PayloadSize = payloadSize });
        var datagram = new byte[65535];
        int length = await udp.ReceiveAsync(datagram, SocketFlags.None, new CancellationTokenSource(Limits.Test).Token);
        await server.StopStreamingAsync();
        Assert.That(length, Is.EqualTo(datagramLength));
        Assert.That(datagram[..4], Is.EqualTo(Hex.Parse(headerHex)));
    }

    [Test]
    public async Task SequenceNumbers_WrapFrom0xFFFFTo1()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        // Everything but the packets around the wrap is dropped, so the numbers advance without sending.
        var wrapped = false;
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            Pacing = Pacing.Unthrottled,
            DropPacket = seq =>
            {
                wrapped |= seq == 65535;
                return !(seq is 65534 or 65535 || (wrapped && seq is 1 or 2));
            },
        });
        await Eventually.ThatAsync(() => c.Packets.Count >= 4);
        await server.StopStreamingAsync();
        var infos = c.Packets.Take(4).Select(p => p.Info).ToArray();
        Assert.That(infos.Select(i => i.Sequence), Is.EqualTo(new ushort[] { 65534, 65535, 1, 2 }));
        Assert.That(infos.Select(i => i.GapBefore), Has.All.EqualTo(0));
    }

    [Test]
    public async Task Restart_StopsPreviousStream_AndStartsNewCapture()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var first = new PacketCollector();
        using var second = new PacketCollector();
        await server.StartStreamingAsync(first.EndPoint);
        await Eventually.ThatAsync(() => first.Packets.Count >= 3);
        await server.StartStreamingAsync(second.EndPoint);
        await Eventually.ThatAsync(() => second.Packets.Count >= 3);
        await server.StopStreamingAsync();
        Assert.That(second.Packets.First().Info.Sequence, Is.EqualTo((ushort)0));
        Assert.That(second.Packets.First().Info.IsCaptureStart, Is.True);
        await Task.Delay(100);
        var count = first.Packets.Count;
        await Task.Delay(200);
        Assert.That(first.Packets, Has.Count.EqualTo(count));
    }

    [Test]
    public async Task StopStreaming_ReturnsOnlyAfterTheLoopEnded_AndCanBeRepeated()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await server.StopStreamingAsync();   // nothing runs yet
        using var c = new PacketCollector();
        var calls = 0;
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            DropPacket = _ => { Interlocked.Increment(ref calls); return false; },
        });
        await Eventually.ThatAsync(() => Volatile.Read(ref calls) >= 3);
        await server.StopStreamingAsync();
        var atStop = Volatile.Read(ref calls);
        await Task.Delay(100);
        Assert.That(Volatile.Read(ref calls), Is.EqualTo(atStop));
        await server.StopStreamingAsync();
    }

    [Test]
    public async Task StartStreaming_RejectsInvalidParametersAndKeepsTheRunningStream()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint);
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);

        await Assert.ThatAsync(() => server.StartStreamingAsync(null!), Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000)),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(new IPEndPoint(IPAddress.IPv6Loopback, 5000)),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Format = SampleFormat.Unknown }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = 3 }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Format = SampleFormat.Int24, PayloadSize = 5 }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = 65504 }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 0 }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = double.NaN }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Channels = 0 }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = 11, Timestamps = true }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Packets = [] }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Packets = [new byte[4], new byte[6]] }),
            Throws.InstanceOf<ArgumentException>());
        await Assert.ThatAsync(
            () => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Packets = [new byte[65500]], Timestamps = true }),
            Throws.InstanceOf<ArgumentException>());

        var count = c.Packets.Count;
        await Eventually.ThatAsync(() => c.Packets.Count > count);
    }

    [Test]
    public async Task StartStreaming_AfterDispose_Throws()
    {
        var server = new NetSdrTestServer();
        await server.StartAsync();
        await server.DisposeAsync();
        using var c = new PacketCollector();
        await Assert.ThatAsync(() => server.StartStreamingAsync(c.EndPoint), Throws.TypeOf<ObjectDisposedException>());
        await server.StopStreamingAsync();   // still a no-op
    }

    [Test]
    public async Task Dispose_StopsManualStream()
    {
        var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint);
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await server.DisposeAsync();
        await AssertNoMorePacketsAsync(c);
    }

    [Test]
    public async Task FailingSource_IsReportedAtDispose()
    {
        var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        var failing = new TaskCompletionSource();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions
        {
            Source = (_, _, _) =>
            {
                failing.TrySetResult();
                throw new FormatException("source failed");
            },
        });
        await failing.Task.WaitAsync(Limits.Test);
        await server.StopStreamingAsync();
        var error = await Assert.ThrowsAsync<FormatException>(() => server.DisposeAsync().AsTask());
        Assert.That(error.Message, Is.EqualTo("source failed"));
        Assert.That(c.Packets, Is.Empty);
    }

    [Test]
    public async Task AutoStream_StopsWhenClientDisconnects()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await server.DisconnectClientAsync();
        await Loopback.AssertServerFreeAsync(server);   // the next client is served only after the first one is done
        await AssertNoMorePacketsAsync(c);
    }

    [Test]
    public async Task AutoStream_StopsWhenServerIsDisposed()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await server.DisposeAsync();
        await AssertNoMorePacketsAsync(c);
    }

    [Test]
    public async Task ManualStream_OutlivesTheClient()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint);
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await client.DisposeAsync();
        await Eventually.ThatAsync(() => !client.IsConnected);
        await Loopback.AssertServerFreeAsync(server);
        var count = c.Packets.Count;
        await Eventually.ThatAsync(() => c.Packets.Count >= count + 3);
    }

    [Test]
    public async Task AutoStream_StopEcho_ComesAfterTheLoopEnded()
    {
        var calls = 0;
        var (server, client) = await Loopback.StartAsync(s => s.Stream.DropPacket = _ =>
        {
            Interlocked.Increment(ref calls);
            return false;
        });
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => Volatile.Read(ref calls) >= 3);
        await client.SetAsync(ReceiverState.Stop);
        var atStop = Volatile.Read(ref calls);
        await Task.Delay(100);
        Assert.That(Volatile.Read(ref calls), Is.EqualTo(atStop));
    }

    [Test]
    public async Task AutoStream_StateIsStored_AndAnswersGet()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: true));
        Assert.That((await client.GetAsync<ReceiverState>()).IsRunning, Is.True);
        await client.SetAsync(ReceiverState.Stop);
        Assert.That((await client.GetAsync<ReceiverState>()).IsRunning, Is.False);
    }

    [Test]
    public async Task AutoStream_SecondRun_RestartsTheCapture()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: true));
        await Eventually.ThatAsync(() => c.Packets.Count(p => p.Info.IsCaptureStart) >= 2);
        var restart = c.Packets.Last(p => p.Info.IsCaptureStart);
        Assert.That(restart.Info.Format, Is.EqualTo(SampleFormat.Int24));
    }

    [Test]
    public async Task AutoStream_StreamOptionsWinOverState_ForFormatAndRate()
    {
        var (server, client) = await Loopback.StartAsync(s =>
        {
            s.Stream.Format = SampleFormat.Int24;
            s.Stream.SampleRate = 2_560;   // 240 samples a packet: about 10 packets a second
        });
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new OutputSampleRate(0, 1_000_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await Task.Delay(300);
        Assert.That(c.Packets.First().Info.Format, Is.EqualTo(SampleFormat.Int24));
        Assert.That(c.Packets.Count, Is.InRange(1, 6));
    }

    [Test]
    public async Task AutoStream_SampleRateFromState_PacesThePackets()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new OutputSampleRate(0, 2_560));   // 256 samples a packet: 10 packets a second
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await Task.Delay(400);
        await client.SetAsync(ReceiverState.Stop);
        Assert.That(c.Packets.Count, Is.InRange(2, 8));
    }

    [Test]
    public async Task AutoStream_WithoutUdpAddress_TargetsTheTcpClient()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        // The server sends data to the port the TCP client connected from, so the receiver and the TCP client need the
        // same local port: the receiver binds a port, then the TCP client binds that same number.
        // Windows reserves blocks of TCP ports (Hyper-V, WinNAT) separately from the UDP blocks, and it hands out
        // ephemeral UDP ports one after another, so asking for port 0 can walk into a TCP-reserved block again and
        // again. A random port per attempt, and a generous number of attempts, makes that practically impossible.
        const int maxAttempts = 50;
        for (var attempt = 1; ; attempt++)
        {
            int port = Random.Shared.Next(49152, 65536);
            var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var receiver = new NetSdrDataReceiver(
                (in DataPacketInfo _, ReadOnlySpan<byte> samples) => received.TrySetResult(samples.Length));
            TcpClient? tcp = null;
            try
            {
                receiver.Bind(new IPEndPoint(IPAddress.Loopback, port));
                receiver.Start();
                tcp = new TcpClient(new IPEndPoint(IPAddress.Loopback, port));
                await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            }
            catch (SocketException) when (attempt < maxAttempts)
            {
                tcp?.Dispose();
                continue;    // the UDP or the TCP port is unavailable, try another pair
            }

            using (tcp)
            {
                var run = ControlFrames.Request(RequestType.Set, ReceiverState.Start(complex: true, bits24: false));
                await tcp.GetStream().WriteAsync(run);
                Assert.That(await received.Task.WaitAsync(Limits.Test), Is.EqualTo(1024));
            }

            return;
        }
    }

    [Test]
    public async Task AutoStream_UnusableTarget_IsNaked_AndNothingStarts()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000)));
        await Assert.ThatAsync(
            () => client.SetAsync(ReceiverState.Start(complex: true, bits24: false)),
            Throws.TypeOf<NetSdrNakException>());
        await client.SetAsync(new DataOutputUdpAddress(0, 0));
        await Assert.ThatAsync(
            () => client.SetAsync(ReceiverState.Start(complex: true, bits24: false)),
            Throws.TypeOf<NetSdrNakException>());
        Assert.That(c.Packets, Is.Empty);
        // The rejected Run was not stored, and the server still works.
        await Assert.ThatAsync(() => client.GetAsync<ReceiverState>(), Throws.TypeOf<NetSdrNakException>());
        // Both rejections came from the server refusing the target, and it recorded why. The Get above is a NAK it
        // chose to send.
        Assert.That(server.HandlerErrors, Has.Count.EqualTo(2));
        Assert.That(server.HandlerErrors, Has.All.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task AutoStream_HandlerForReceiverState_TakesOver()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest<ReceiverState>(_ => ControlReply.Echo));
        await using var _ = server;
        await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Task.Delay(300);
        Assert.That(c.Packets, Is.Empty);
        // The caller can still start the stream by hand.
        await server.StartStreamingAsync(c.EndPoint);
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
    }

    // The stream has stopped when no packet arrives after the ones already on their way have been delivered.
    private static async Task AssertNoMorePacketsAsync(PacketCollector c)
    {
        await Task.Delay(100);
        var count = c.Packets.Count;
        await Task.Delay(300);
        Assert.That(c.Packets, Has.Count.EqualTo(count));
    }
}
