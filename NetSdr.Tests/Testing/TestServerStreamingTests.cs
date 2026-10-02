using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetSdr.Data;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Tests.Data;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class TestServerStreamingTests
{
    static short[] Shorts(byte[] samples) => MemoryMarshal.Cast<byte, short>(samples).ToArray();

    [Fact]
    public async Task ManualStream_Int16_CounterData()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 256_000 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        var packets = c.Packets.ToArray();
        Assert.Equal((SampleFormat.Int16, (ushort)0, true), (packets[0].Info.Format, packets[0].Info.Sequence, packets[0].Info.IsCaptureStart));
        Assert.Equal(1024, packets[0].Samples.Length);
        var first = Shorts(packets[0].Samples);
        Assert.Equal((0, -1, 255, -256), (first[0], first[1], first[510], first[511]));
        Assert.Equal(256, Shorts(packets[1].Samples)[0]);
    }

    [Fact]
    public async Task ManualStream_Int24_CounterData()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { Format = SampleFormat.Int24, SampleRate = 240_000 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        var second = c.Packets.ElementAt(1);
        Assert.Equal((SampleFormat.Int24, 1440), (second.Info.Format, second.Samples.Length));
        Assert.Equal(Hex.Parse("F0 00 00 0F FF FF"), second.Samples[..6]);   // sample 240
    }

    [Fact]
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
        Assert.Equal(2, five.Info.GapBefore);
        Assert.Equal(5 * 256, Shorts(five.Samples)[0]);
        Assert.Equal(2, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Jumbo_DeliveredWithoutValidation()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var strict = new PacketCollector();
        using var loose = new PacketCollector(new DataReceiverOptions { ValidateLength = false });
        var options = new StreamOptions { PayloadSize = 8996, SampleRate = 22_490 };
        await server.StartStreamingAsync(loose.EndPoint, options);
        await Eventually.ThatAsync(() => loose.Packets.Count >= 1);
        await server.StartStreamingAsync(strict.EndPoint, options);
        await Eventually.ThatAsync(() => strict.Receiver.Statistics.Rejected >= 1);
        await server.StopStreamingAsync();
        Assert.Equal(8996, loose.Packets.First().Samples.Length);
        Assert.Empty(strict.Packets);
    }

    [Theory]
    [InlineData(false, DataOutputPacketSize.Large, 1028)]
    [InlineData(false, DataOutputPacketSize.Small, 516)]
    [InlineData(true, DataOutputPacketSize.Large, 1444)]
    [InlineData(true, DataOutputPacketSize.Small, 388)]
    public async Task AutoStream_PacketSizeFromState(bool bits24, byte size, int datagramLength)
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new DataOutputPacketSize(size));
        await client.SetAsync(new OutputSampleRate(0, 100_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        Assert.Equal(datagramLength - 4, c.Packets.First().Samples.Length);
        Assert.Equal(bits24 ? SampleFormat.Int24 : SampleFormat.Int16, c.Packets.First().Info.Format);
    }

    [Fact]
    public async Task AutoStream_StopEndsStream()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await client.SetAsync(ReceiverState.Stop);
        await Task.Delay(100);
        var count = c.Packets.Count;
        await Task.Delay(300);
        Assert.Equal(count, c.Packets.Count);
    }

    [Fact]
    public async Task AutoStream_ZeroIp_UsesClientAddress()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new DataOutputUdpAddress(0, (ushort)c.EndPoint.Port));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
    }

    [Fact]
    public async Task AutoStream_ExplicitOptionsWin()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Stream.PayloadSize = 200);
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        Assert.Equal(200, c.Packets.First().Samples.Length);
    }

    [Fact]
    public async Task AutoStream_Disabled_NoData()
    {
        var (server, client) = await Loopback.StartAsync(s => s.AutoStream = false);
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Task.Delay(300);
        Assert.Empty(c.Packets);
    }

    [Fact]
    public async Task RealTimePacing_LimitsRate()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 2_560 });   // 10 packets/s
        await Task.Delay(500);
        await server.StopStreamingAsync();
        Assert.InRange(c.Packets.Count, 2, 10);
    }

    // The tests below go beyond the brief: the carried client-left and dispose rules, and the rules the brief
    // states but its tests do not exercise.

    [Fact]
    public async Task StreamOptions_And_Server_Defaults()
    {
        await using var server = new NetSdrTestServer();
        Assert.True(server.AutoStream);
        var options = new StreamOptions();
        Assert.Null(options.Format);
        Assert.Null(options.PayloadSize);
        Assert.Null(options.SampleRate);
        Assert.Null(options.DropPacket);
        Assert.Equal((1, Pacing.RealTime), (options.Channels, options.Pacing));
        var b = new byte[4];
        options.Source(b, 5, SampleFormat.Int16);
        Assert.Equal(Hex.Parse("05 00 FA FF"), b);
    }

    [Fact]
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

    [Fact]
    public async Task RealTimePacing_ChannelsShareTheSampleRate()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        // 256 samples a packet over two channels at 2560 Hz: 50 ms a packet, where one channel would need 100 ms.
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 2_560, Channels = 2 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 10);
        await server.StopStreamingAsync();
        var packets = c.Packets.ToArray();
        var elapsed = Stopwatch.GetElapsedTime(packets[0].Info.Timestamp, packets[9].Info.Timestamp);
        Assert.InRange(elapsed.TotalMilliseconds, 300, 700);   // 450 ms; 900 ms for one channel
    }

    [Fact]
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
        Assert.Equal(200, c.Packets.First().Samples.Length);
        Assert.Equal(new byte[] { 0, 0 }, c.Packets.First().Samples[198..]);
    }

    [Fact]
    public async Task ManualStream_PayloadRemainder_IsZero()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = 10 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await server.StopStreamingAsync();
        Assert.Equal(Hex.Parse("00 00 FF FF 01 00 FE FF 00 00"), c.Packets.First().Samples);
    }

    [Fact]
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
        Assert.Equal(Hex.Parse("01 02 03 04 05 06 07 08 01 02 03 04"), c.Packets.ElementAt(0).Samples);
        Assert.Equal(Hex.Parse("05 06 07 08 01 02 03 04 05 06 07 08"), c.Packets.ElementAt(1).Samples);
    }

    [Theory]
    [InlineData(SampleFormat.Int24, null, 1444, "A4 85 00 00")]     // the spec's own example
    [InlineData(SampleFormat.Int16, 8187, 8191, "FF 9F 00 00")]     // the longest length the field holds
    [InlineData(SampleFormat.Int16, 8190, 8194, "00 80 00 00")]     // 8194 is the standard encoding of 0
    [InlineData(SampleFormat.Int16, 8996, 9000, "00 80 00 00")]     // a jumbo packet is written as 0 as well
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
        Assert.Equal(datagramLength, length);
        Assert.Equal(Hex.Parse(headerHex), datagram[..4]);
    }

    [Fact]
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
        Assert.Equal(new ushort[] { 65534, 65535, 1, 2 }, infos.Select(i => i.Sequence));
        Assert.All(infos, i => Assert.Equal(0, i.GapBefore));
    }

    [Fact]
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
        Assert.Equal((ushort)0, second.Packets.First().Info.Sequence);
        Assert.True(second.Packets.First().Info.IsCaptureStart);
        await Task.Delay(100);
        var count = first.Packets.Count;
        await Task.Delay(200);
        Assert.Equal(count, first.Packets.Count);
    }

    [Fact]
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
        Assert.Equal(atStop, Volatile.Read(ref calls));
        await server.StopStreamingAsync();
    }

    [Fact]
    public async Task StartStreaming_RejectsInvalidParametersAndKeepsTheRunningStream()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint);
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(new IPEndPoint(IPAddress.IPv6Loopback, 5000)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Format = SampleFormat.Unknown }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = 3 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Format = SampleFormat.Int24, PayloadSize = 5 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(c.EndPoint, new StreamOptions { PayloadSize = 65504 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 0 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = double.NaN }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartStreamingAsync(c.EndPoint, new StreamOptions { Channels = 0 }));

        var count = c.Packets.Count;
        await Eventually.ThatAsync(() => c.Packets.Count > count);
    }

    [Fact]
    public async Task StartStreaming_AfterDispose_Throws()
    {
        var server = new NetSdrTestServer();
        await server.StartAsync();
        await server.DisposeAsync();
        using var c = new PacketCollector();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.StartStreamingAsync(c.EndPoint));
        await server.StopStreamingAsync();   // still a no-op
    }

    [Fact]
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

    [Fact]
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
        Assert.Equal("source failed", error.Message);
        Assert.Empty(c.Packets);
    }

    [Fact]
    public async Task AutoStream_StopsWhenClientDisconnects()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await server.DisconnectClientAsync();
        await Loopback.AssertServerFreeAsync(server);   // the next client is served only after the first one is done
        await AssertNoMorePacketsAsync(c);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task AutoStream_StopEcho_ComesAfterTheLoopEnded()
    {
        var calls = 0;
        var (server, client) = await Loopback.StartAsync(s => s.Stream.DropPacket = _ =>
        {
            Interlocked.Increment(ref calls);
            return false;
        });
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => Volatile.Read(ref calls) >= 3);
        await client.SetAsync(ReceiverState.Stop);
        var atStop = Volatile.Read(ref calls);
        await Task.Delay(100);
        Assert.Equal(atStop, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task AutoStream_StateIsStored_AndAnswersGet()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: true));
        Assert.True((await client.GetAsync<ReceiverState>()).IsRunning);
        await client.SetAsync(ReceiverState.Stop);
        Assert.False((await client.GetAsync<ReceiverState>()).IsRunning);
    }

    [Fact]
    public async Task AutoStream_SecondRun_RestartsTheCapture()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: true));
        await Eventually.ThatAsync(() => c.Packets.Count(p => p.Info.IsCaptureStart) >= 2);
        var restart = c.Packets.Last(p => p.Info.IsCaptureStart);
        Assert.Equal(SampleFormat.Int24, restart.Info.Format);
    }

    [Fact]
    public async Task AutoStream_StreamOptionsWinOverState_ForFormatAndRate()
    {
        var (server, client) = await Loopback.StartAsync(s =>
        {
            s.Stream.Format = SampleFormat.Int24;
            s.Stream.SampleRate = 2_560;   // 240 samples a packet: about 10 packets a second
        });
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new OutputSampleRate(0, 1_000_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await Task.Delay(300);
        Assert.Equal(SampleFormat.Int24, c.Packets.First().Info.Format);
        Assert.InRange(c.Packets.Count, 1, 6);
    }

    [Fact]
    public async Task AutoStream_SampleRateFromState_PacesThePackets()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new OutputSampleRate(0, 2_560));   // 256 samples a packet: 10 packets a second
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        await Task.Delay(400);
        await client.SetAsync(ReceiverState.Stop);
        Assert.InRange(c.Packets.Count, 2, 8);
    }

    [Fact]
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
                Assert.Equal(1024, await received.Task.WaitAsync(Limits.Test));
            }

            return;
        }
    }

    [Fact]
    public async Task AutoStream_UnusableTarget_IsNaked_AndNothingStarts()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000)));
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.SetAsync(ReceiverState.Start(complex: true, bits24: false)));
        await client.SetAsync(new DataOutputUdpAddress(0, 0));
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.SetAsync(ReceiverState.Start(complex: true, bits24: false)));
        Assert.Empty(c.Packets);
        // The rejected Run was not stored, and the server still works.
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ReceiverState>());
        // Both rejections came from the server refusing the target, and it recorded why. The Get above is a NAK it
        // chose to send.
        Assert.Equal(2, server.HandlerErrors.Count);
        Assert.All(server.HandlerErrors, error => Assert.IsType<ArgumentException>(error));
    }

    [Fact]
    public async Task AutoStream_HandlerForReceiverState_TakesOver()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest<ReceiverState>(_ => ControlReply.Echo));
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Task.Delay(300);
        Assert.Empty(c.Packets);
        // The caller can still start the stream by hand.
        await server.StartStreamingAsync(c.EndPoint);
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
    }

    // The stream has stopped when no packet arrives after the ones already on their way have been delivered.
    static async Task AssertNoMorePacketsAsync(PacketCollector c)
    {
        await Task.Delay(100);
        var count = c.Packets.Count;
        await Task.Delay(300);
        Assert.Equal(count, c.Packets.Count);
    }
}
