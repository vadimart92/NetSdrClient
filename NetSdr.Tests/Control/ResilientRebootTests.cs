using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientRebootTests
{
    const string Outer = "NetSdr.Control.ResilientControlClient";

    /// <summary>A server, a client with a FakeRebooter that boots the server through CloseOnAccept for 200 ms, Fast options.</summary>
    static async Task<(NetSdrTestServer Server, ResilientControlClient Client, FakeRebooter Rebooter)> StartAsync(
        FakeLoggerFactory logs, Action<ResilientControlClientOptions>? configure = null, Action<NetSdrTestServer>? setup = null)
    {
        var options = Resilient.Fast(logs);
        configure?.Invoke(options);
        FakeRebooter rebooter = null!;
        var (server, client) = await Resilient.StartAsync(options, s =>
        {
            setup?.Invoke(s);
            options.WithRebooter(rebooter = FakeRebooter.Booting(s, TimeSpan.FromMilliseconds(200)), options.RecoveryPolicy);
        });
        return (server, client, rebooter);
    }

    [Fact]
    public async Task RebootAsync_Connected_RestoresWithAfterReboot()
    {
        var logs = new FakeLoggerFactory();
        RebootKind? seen = null;
        var (server, client, rebooter) = await StartAsync(logs, o => o.ConnectionRestored = (ctx, _) => { seen = ctx.AfterReboot; return Task.CompletedTask; });
        await using (server)
        await using (client)
        {
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);
            Assert.True(client.IsConnected);
            Assert.Equal(RebootKind.Soft, seen);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            var context = Assert.Single(rebooter.Contexts);
            Assert.Equal((true, client.RemoteEndPoint, $"127.0.0.1:{server.Port}"), (context.Requested, context.LastRemoteEndPoint, context.Target));
            Assert.Equal((LogLevel.Information, "Soft"), (Assert.Single(logs.Events(1113)).Level, logs.Events(1113)[0].Value("Kind")));
            Assert.Single(logs.Events(1116));
            Assert.Single(logs.Events(1105));
            Assert.Empty(logs.Events(1103));
            Assert.Empty(logs.Events(1114));
        }
    }

    [Fact]
    public async Task RebootAsync_JoinsRunningReboot()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        await using (client)
        {
            var soft = client.RebootAsync(RebootKind.Soft);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);                 // accepted: the 300 ms boot wait runs
            var hard = client.RebootAsync(RebootKind.Hard);                             // joins, whatever its kind
            await Task.WhenAll(soft, hard).WaitAsync(Limits.Test);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            Assert.Equal(2, logs.Events(1113).Count);
            Assert.Single(logs.Events(1105));
        }
    }

    [Fact]
    public async Task RebootAsync_TransportFails_CallerGetsException_ClientReconnects()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        rebooter.OnReboot = (_, _, _) => Task.FromException(new ApplicationException("no service"));
        await using (server)
        await using (client)
        {
            await Assert.ThrowsAsync<ApplicationException>(() => client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test));
            Assert.IsType<ApplicationException>(Assert.Single(logs.Events(1115)).Exception);
            await Eventually.ThatAsync(() => client.IsConnected && logs.Events(1105).Count == 1);
            Assert.Empty(logs.Events(1116));
            Assert.Empty(logs.Events(1103));
        }
    }

    [Fact]
    public async Task RebootAsync_CallerCancels_RebootStillHappens()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rebooter.OnReboot = async (_, _, _) => { entered.TrySetResult(); await gate.Task; };
        await using (server)
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            var reboot = client.RebootAsync(RebootKind.Soft, cancel.Token);
            await entered.Task.WaitAsync(Limits.Test);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reboot.WaitAsync(Limits.Test));
            gate.SetResult();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            Assert.Single(logs.Events(1116));
        }
    }

    [Fact]
    public async Task RebootAsync_CommandInFlight_RetriedAfterRestore()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs, o => o.ResponseTimeout = TimeSpan.FromSeconds(1),
            s => s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Silent)));
        await using (server)
        await using (client)
        {
            var set = client.SetAsync(new AfGain(0, 7));
            await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == AfGain.Code));   // in flight, unanswered
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);
            Assert.Equal(7, (await set.WaitAsync(Limits.Test)).Level);
            Assert.Equal(2, server.Received.Count(r => r.Code == AfGain.Code));
            Assert.Single(logs.Events(1100));
        }
    }

    [Fact]
    public async Task RebootAsync_WithoutRebooter_Throws()
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast());
        await using (server)
        await using (client)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => { _ = client.RebootAsync(RebootKind.Soft); });
            Assert.Equal("No Rebooter is configured; set ResilientControlClientOptions.Rebooter.", ex.Message);
        }
    }

    [Fact]
    public async Task RebootAsync_AfterDispose_Throws()
    {
        var (server, client, _) = await StartAsync(new FakeLoggerFactory());
        await using (server)
        {
            await client.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => { _ = client.RebootAsync(RebootKind.Soft); });
        }

        // After a give-up: the failure as the inner exception, like a command.
        var connector = new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam().WithRebooter(new FakeRebooter());
        options.ReconnectAttempts = 1;
        var (gaveUp, device) = await connector.StartAsync(options);
        device.CloseRemote();
        await Assert.ThrowsAsync<IOException>(() => gaveUp.Completion.WaitAsync(Limits.Test));
        Assert.IsType<IOException>(Assert.Throws<InvalidOperationException>(() => { _ = gaveUp.RebootAsync(RebootKind.Soft); }).InnerException);
        await gaveUp.DisposeAsync();
    }

    [Fact]
    public async Task RebootAsync_InsideCallback_GivesUp()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        Exception? thrown = null;
        var started = await StartAsync(logs, o => o.ConnectionRestored = (_, _) =>
        {
            try { _ = client!.RebootAsync(RebootKind.Soft); } catch (InvalidOperationException e) { thrown = e; }
            return Task.CompletedTask;
        });
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await started.Server.DisconnectClientAsync();
            var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.Same(thrown, failure.InnerException);
            Assert.Empty(started.Rebooter.Calls);
        }

        Assert.Equal("ConnectionRestored called the ResilientControlClient", Assert.Single(logs.Events(1106)).Value("Reason"));
    }

    [Fact]
    public async Task RebootAsync_PreCancelledToken_RegistersNothing()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        await using (client)
        {
            var reboot = client.RebootAsync(RebootKind.Soft, new CancellationToken(canceled: true));
            Assert.True(reboot.IsCanceled);
            await Task.Delay(300);
            Assert.Empty(rebooter.Calls);
            Assert.True(client.IsConnected);
            Assert.Empty(logs.Events(1113));
        }
    }

    // Review Focus 4.
    [Fact]
    public async Task RebootAsync_ParallelCallers_OneReboot()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        await using (client)
        {
            var tasks = new Task[16];
            Parallel.For(0, tasks.Length, i => tasks[i] = client.RebootAsync(i % 2 == 0 ? RebootKind.Soft : RebootKind.Hard));
            await Task.WhenAll(tasks).WaitAsync(Limits.Test);
            Assert.Single(rebooter.Calls);
            Assert.Single(logs.Events(1116));
            Assert.Equal(16, logs.Events(1113).Count);
            Assert.True(client.IsConnected);
        }
    }

    // Review Focus 3.
    [Fact]
    public async Task PolicyBlocks_NothingElseIsHeld()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter { BootTime = TimeSpan.Zero });
        using var gate = new ManualResetEventSlim();
        var inPolicy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new RecordingPolicy { Decide = _ => { inPolicy.TrySetResult(); gate.Wait(Limits.Test); return RecoveryAction.Continue; } };
        bool back = false;
        var connector = new PipeConnector(time)
        {
            Before = (n, _) => n == 1 || Volatile.Read(ref back) ? Task.CompletedTask : Resilient.Refused(),
            Serve = d => d.NakEverythingAsync(),
        };
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter, policy);
        options.CommandTimeout = Timeout.InfiniteTimeSpan;
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            device.CloseRemote();
            await inPolicy.Task.WaitAsync(Limits.Test);                                // the policy blocks the supervisor now
            Assert.False(client.IsConnected);                                          // no lock is held by it
            var command = client.GetAsync<InterfaceVersion>();
            var reboot = client.RebootAsync(RebootKind.Soft);                          // registers at once
            Assert.False(command.IsCompleted || reboot.IsCompleted);
            Volatile.Write(ref back, true);
            gate.Set();
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1 && logs.Events(1105).Count == 1, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<NetSdrNakException>(() => command.WaitAsync(Limits.Test));   // the pipe NAKs it after the restore
            await reboot.WaitAsync(Limits.Test);
        }
    }

    [Fact]
    public async Task Logging_1113To1117_LevelsAndCategory()
    {
        var logs = new FakeLoggerFactory();
        int decisions = 0;
        var policy = new RecordingPolicy { Decide = _ => Interlocked.Increment(ref decisions) == 1 ? throw new ApplicationException("policy") : RecoveryAction.Reboot(RebootKind.Soft) };
        var (server, client, rebooter) = await StartAsync(logs, o =>
        {
            (o.ResponseTimeout, o.LateReplyTimeout, o.RecoveryPolicy) = (TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), policy);
        });
        var booting = rebooter.OnReboot;
        await using (server)
        await using (client)
        {
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);                          // 1113, 1116
            rebooter.OnReboot = (_, _, _) => Task.FromException(new ApplicationException("no service"));
            await Assert.ThrowsAsync<ApplicationException>(() => client.RebootAsync(RebootKind.Hard).WaitAsync(Limits.Test));   // 1113, 1115
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 2);
            rebooter.OnReboot = booting;
            server.Availability = ServerAvailability.Silent;                                            // 1102, 1103, then 1117, 1114, 1116
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 3);
        }

        void Expect(int id, int count, LogLevel level)
        {
            Assert.Equal(count, logs.Events(id).Count);
            Assert.All(logs.Events(id), r => Assert.Equal((level, Outer), (r.Level, r.Category)));
        }

        Expect(1113, 2, LogLevel.Information);
        Expect(1114, 1, LogLevel.Warning);
        Expect(1115, 1, LogLevel.Warning);
        Expect(1116, 2, LogLevel.Information);
        Expect(1117, 1, LogLevel.Warning);
        Assert.Equal(("Soft", "2", "Verify"), (logs.Events(1114)[0].Value("Kind"), logs.Events(1114)[0].Value("FailedAttempts"), logs.Events(1114)[0].Value("Phase")));
        Assert.Empty(logs.Events(1106));
    }
}
