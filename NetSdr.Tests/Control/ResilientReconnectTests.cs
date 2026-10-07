using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientReconnectTests
{
    [Fact]
    public async Task LostDuringCommand_RetriedAfterRestore()
    {
        var logs = new FakeLoggerFactory();
        var options = Resilient.Fast(logs);
        options.ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new RfGain(0, -10), ct);
        var (server, client) = await Resilient.StartAsync(options, s => s.OnRequest(AfGain.Code, Resilient.DropOnce(s)));
        await using (server)
        await using (client)
        {
            Assert.Equal(7, (await client.SetAsync(new AfGain(0, 7)).WaitAsync(Limits.Test)).Level);
            Assert.Equal(new[] { AfGain.Code, RfGain.Code, AfGain.Code }, server.Received.WithoutStatus().Select(r => r.Code));
            Assert.Equal("ConnectionLost", Assert.Single(logs.Events(1100)).Value("Reason"));
            Assert.Single(logs.Events(1105));
        }
    }

    [Fact]
    public async Task DeviceDroppedRequest_ConnectionReplaced_CommandResent()
    {
        var logs = new FakeLoggerFactory();
        int restored = 0;
        var options = Resilient.Fast(logs);
        options.ConnectionRestored = (ctx, ct) =>
        {
            Interlocked.Increment(ref restored);
            return ctx.Client.SetAsync(new RfGain(0, -10), ct);
        };
        var (server, client) = await Resilient.StartAsync(options, s => s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Silent)));
        await using (server)
        await using (client)
        {
            Assert.Equal(3, (await client.SetAsync(new AfGain(0, 3)).WaitAsync(Limits.Test)).Level);
            Assert.Single(logs.Events(1102));
            Assert.Single(logs.Events(1103));
            Assert.Single(logs.Events(1105));
            Assert.Equal(1, restored);
            Assert.Equal(new[] { AfGain.Code, RfGain.Code, AfGain.Code }, server.Received.WithoutStatus().Select(r => r.Code));
        }
    }

    [Fact]
    public async Task IdleDrop_ReconnectsProactively()
    {
        var logs = new FakeLoggerFactory();
        int restored = 0;
        ConnectionRestoredContext? seen = null;
        var options = Resilient.Fast(logs);
        options.ConnectionRestored = (ctx, _) => { seen = ctx; Interlocked.Increment(ref restored); return Task.CompletedTask; };
        var (server, client) = await Resilient.StartAsync(options);
        await using (server)
        await using (client)
        {
            int oldPort = client.LocalEndPoint!.Port;
            await server.DisconnectClientAsync();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            Assert.True(client.IsConnected);
            Assert.NotEqual(oldPort, client.LocalEndPoint!.Port);
            Assert.Equal(LogLevel.Warning, Assert.Single(logs.Events(1103)).Level);
            Assert.Equal((LogLevel.Information, "1"), (logs.Events(1105)[0].Level, logs.Events(1105)[0].Value("Attempts")));
            Assert.Equal(1, restored);
            Assert.NotEqual(default, seen!.LostAt);
            Assert.IsAssignableFrom<IOException>(seen.Cause);
        }
    }

    // The test server serves one client at a time: a plain client queued behind ours takes the server once ours drops.
    static async Task<NetSdrControlClient> OccupyAsync(NetSdrTestServer server)
    {
        var blocker = new NetSdrControlClient();
        await blocker.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        await server.DisconnectClientAsync();
        return blocker;
    }

    [Fact]
    public async Task Verification_OneClientServerBusy()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            var blocker = await OccupyAsync(server);
            await Eventually.ThatAsync(() => logs.Events(1104).Any(r => r.Value("Phase") == "Verify"));
            Assert.False(client.IsConnected);
            await blocker.DisposeAsync();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
        }
    }

    [Fact]
    public async Task IsConnected_And_EndPoints()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            var (local, remote) = (client.LocalEndPoint, client.RemoteEndPoint);
            var blocker = await OccupyAsync(server);
            await Eventually.ThatAsync(() => logs.Events(1104).Count >= 1);
            Assert.False(client.IsConnected);
            Assert.Equal((local, remote), (client.LocalEndPoint, client.RemoteEndPoint));   // the lost connection's ends
            await blocker.DisposeAsync();
            await Eventually.ThatAsync(() => client.IsConnected);
            Assert.NotEqual(local!.Port, client.LocalEndPoint!.Port);
            Assert.Equal(remote, client.RemoteEndPoint);
        }
    }

    [Fact]
    public async Task Unsolicited_SpansReconnects_InOrder()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            await server.SendUnsolicitedAsync(new AfGain(0, 1));
            await server.SendUnsolicitedAsync(new AfGain(0, 2));
            await server.DisconnectClientAsync();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            await server.SendUnsolicitedAsync(new AfGain(0, 3));
            await server.SendUnsolicitedAsync(new AfGain(0, 4));
            var levels = new List<byte>();
            while (levels.Count < 4)
            {
                var m = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
                if (m.Type == ReplyType.Unsolicited) levels.Add(m.As<AfGain>().Level);
            }

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, levels);
            Assert.False(client.Unsolicited.Completion.IsCompleted);
            await client.DisposeAsync();
            await client.Unsolicited.Completion.WaitAsync(Limits.Test);
        }
    }

    [Fact]
    public async Task ConcurrentCommands_AcrossADrop_InCallOrder()
    {
        int sets = 0;
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s => s.OnRequest(AfGain.Code, request =>
        {
            if (Interlocked.Increment(ref sets) != 5) return ControlReply.Echo;
            _ = s.DisconnectClientAsync();
            return ControlReply.Silent;
        }));
        await using (server)
        await using (client)
        {
            var calls = Enumerable.Range(1, 10).Select(i => client.SetAsync(new AfGain(0, (byte)i))).ToArray();
            var echoes = await Task.WhenAll(calls).WaitAsync(Limits.Test);
            Assert.Equal(Enumerable.Range(1, 10).Select(i => (byte)i), echoes.Select(e => e.Level));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 5, 6, 7, 8, 9, 10 },
                server.Received.Where(r => r.Code == AfGain.Code).Select(r => r.Payload.Span[1]));
        }
    }

    [Fact]
    public async Task Backoff_Schedule_And_AntiFlap()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var connector = new PipeConnector(time) { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var (client, device) = await connector.StartAsync(Resilient.Seam(logs, time));
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));               // the first attempt after this loss may start at once
            device.CloseRemote();
            // The loss and the refused attempt 2 run in real time on pool threads; fake time steps only once Polly has logged
            // 1104 for it and is about to create its 1 s delay. The later attempts fire inline inside Advance and are exact.
            await Eventually.ThatAsync(() => logs.Events(1104).Count == 1);
            await time.AdvanceUntilAsync(() => connector.Attempts >= 6, TimeSpan.FromMilliseconds(100));
            // Starts at 0, 1, 3, 7, 15 s: consecutive gaps, each late by at most a few 100 ms steps of fake time.
            var starts = connector.AttemptTimes.Skip(1).Take(5).ToArray();
            double[] gaps = [1, 2, 4, 8];
            for (int i = 1; i < 5; i++) Assert.InRange((starts[i] - starts[i - 1]).TotalSeconds, gaps[i - 1], gaps[i - 1] + 0.3);
            Assert.Equal(new[] { 1.0, 2, 4, 8 }, logs.Events(1104).Take(4).Select(r => r.Span("Delay").TotalSeconds));
        }

        // A device that accepts, answers the verification and drops at once gets at most one connection a fake second.
        var flapTime = new FakeTimeProvider();
        var flapping = new PipeConnector(flapTime)
        {
            Serve = async d =>
            {
                await d.ReadRequestAsync();
                await d.SendAsync(Resilient.Nak);
                await Task.Delay(20);
                d.CloseRemote();
            },
        };
        var (flap, _) = await flapping.StartAsync(Resilient.Seam(time: flapTime));
        await using (flap)
        {
            for (int i = 0; i < 100; i++)                        // 10 fake seconds
            {
                flapTime.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(5);
            }

            Assert.InRange(flapping.Attempts, 2, 11);
        }
    }

    [Fact]
    public async Task GiveUp_ReconnectAttemptsExhausted()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var connector = new PipeConnector(time) { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam(logs, time);
        options.CommandTimeout = Timeout.InfiniteTimeSpan;
        options.ReconnectAttempts = 3;
        var (client, device) = await connector.StartAsync(options);
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        await Eventually.ThatAsync(() => !client.IsConnected);
        var waiting = client.GetAsync<InterfaceVersion>();
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, TimeSpan.FromMilliseconds(100));
        Assert.Equal(new[] { 1.0, 2 }, logs.Events(1104).Select(r => r.Span("Delay").TotalSeconds));
        var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(Limits.Test)));
        Assert.IsType<SocketException>(failure.InnerException);
        Assert.Contains("after 3 attempt(s)", failure.Message);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => { _ = client.GetAsync<InterfaceVersion>(); }).InnerException);
        Assert.False(client.IsConnected);
        await client.Unsolicited.Completion.WaitAsync(Limits.Test);
        await client.DisposeAsync();
        Assert.Same(failure, client.Completion.Exception!.InnerException);
        var gaveUp = Assert.Single(logs.Events(1106));
        Assert.Equal((LogLevel.Error, "attempts exhausted"), (gaveUp.Level, gaveUp.Value("Reason")));
    }

    [Fact]
    public async Task Dispose_DuringBackoff()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var connector = new PipeConnector(time) { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var (client, device) = await connector.StartAsync(Resilient.Seam(logs, time));
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        await time.AdvanceUntilAsync(() => logs.Events(1104).Count == 1, TimeSpan.FromMilliseconds(100));
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);   // fake time stands still: Polly waits its 1 s
        Assert.True(client.Completion.IsCompletedSuccessfully);
        Assert.Empty(logs.Events(1106));
    }

    [Fact]
    public async Task Dispose_RacingLoss_200Iterations()
    {
        int unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> count = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Any(x => x.StackTrace?.Contains("ResilientControlClient") == true))
                Interlocked.Increment(ref unobserved);
        };
        TaskScheduler.UnobservedTaskException += count;
        try
        {
            var logs = new FakeLoggerFactory();
            await using var server = new NetSdrTestServer();
            await server.StartAsync();
            for (int i = 0; i < 200; i++)
            {
                var client = await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), Resilient.Fast(logs))
                    .WaitAsync(Limits.Test);
                await Task.WhenAll(server.DisconnectClientAsync(), client.DisposeAsync().AsTask()).WaitAsync(Limits.Test);
                Assert.True(client.Completion.IsCompletedSuccessfully);
            }

            Assert.Empty(logs.Events(1106));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Equal(0, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= count;
        }
    }

    // Review Focus 1.
    [Fact]
    public async Task HostName_ReconnectsByName_EndPointsStayIPv4()
    {
        var logs = new FakeLoggerFactory();
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        var client = await ResilientControlClient.ConnectAsync("127.0.0.1", server.Port, Resilient.Fast(logs)).WaitAsync(Limits.Test);
        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint!.Address);
        await server.DisconnectClientAsync();
        await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint!.Address);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, server.Port), client.RemoteEndPoint);
        _ = DataOutputUdpAddress.For(new IPEndPoint(client.LocalEndPoint.Address, 50_001));   // what a ConnectionRestored callback builds
        await client.DisposeAsync();
        Assert.Equal($"127.0.0.1:{server.Port}", Assert.Single(logs.Events(1112)).Value("Target"));
    }

    // Review Focus 2.
    [Fact]
    public async Task LongOutage_SeventyAttempts_DelayCappedThenRecovers()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        bool back = false;
        var connector = new PipeConnector(time)
        {
            Before = (n, _) => n == 1 || Volatile.Read(ref back) ? Task.CompletedTask : Resilient.Refused(),
            Serve = d => d.NakEverythingAsync(),
        };
        var options = Resilient.Seam(logs, time);
        options.UseJitter = true;                                // the production default
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            device.CloseRemote();
            for (int chunk = 1; chunk <= 7; chunk++)             // each AdvanceUntilAsync stays within Limits.Test
                await time.AdvanceUntilAsync(() => logs.Events(1104).Count >= chunk * 10, TimeSpan.FromSeconds(30));
            Assert.All(logs.Events(1104), r => Assert.InRange(r.Span("Delay"), TimeSpan.Zero, TimeSpan.FromSeconds(30)));
            Volatile.Write(ref back, true);
            await time.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, TimeSpan.FromSeconds(30));
            Assert.True(client.IsConnected);
            Assert.Equal((logs.Events(1104).Count + 1).ToString(), logs.Events(1105)[0].Value("Attempts"));
            Assert.Empty(logs.Events(1106));
        }
    }

    // Review Focus 3.
    [Fact]
    public async Task Dispose_FailsWaitingCommands_WithObjectDisposed()
    {
        var connector = new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam();
        options.CommandTimeout = Timeout.InfiniteTimeSpan;
        var (client, device) = await connector.StartAsync(options);
        device.CloseRemote();
        await Eventually.ThatAsync(() => !client.IsConnected);
        var waitingForLink = client.GetAsync<InterfaceVersion>();
        var queued = client.GetAsync<ProductId>();
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waitingForLink.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(Limits.Test));
        Assert.Throws<ObjectDisposedException>(() => { _ = client.GetAsync<InterfaceVersion>(); });

        var (live, pipe) = await new PipeConnector().StartAsync(Resilient.Seam());
        var inFlight = live.GetAsync<ProductId>();
        await pipe.ReadRequestAsync();
        await live.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inFlight.WaitAsync(Limits.Test));
    }
}
