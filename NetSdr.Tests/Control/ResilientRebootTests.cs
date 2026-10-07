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

    /// <summary>A seam on fake time whose attempt 1 is answered by StartAsync; later verifications the test answers itself.</summary>
    static async Task<(ResilientControlClient Client, PipeDevice Device, PipeConnector Connector, FakeRebooter Rebooter, FakeLoggerFactory Logs, FakeTimeProvider Time)>
        SeamAsync(Action<ResilientControlClientOptions>? configure = null, Func<int, bool>? accept = null)
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter { BootTime = TimeSpan.Zero });
        var connector = new PipeConnector(time) { Before = (n, _) => accept is null || accept(n) ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter);
        configure?.Invoke(options);
        var (client, device) = await connector.StartAsync(options);
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        return (client, device, connector, rebooter, logs, time);
    }

    static async Task<PipeDevice> PendingVerifyAsync(PipeConnector connector)
    {
        PipeDevice device = await connector.NextAsync();
        Assert.Equal(Hex.Parse(Resilient.GetStatus), await device.ReadRequestAsync());
        return device;
    }

    [Fact]
    public async Task RebootAsync_DuringBackoff_InterruptsPause()
    {
        bool back = false;
        var (client, _, connector, rebooter, logs, time) = await SeamAsync(o => o.RecoveryPolicy = new RecordingPolicy(), accept: n => n == 1 || Volatile.Read(ref back));
        await using (client)
        {
            DateTimeOffset lost = time.GetUtcNow();
            await time.AdvanceUntilAsync(() => logs.Events(1104).Count == 3, TimeSpan.FromMilliseconds(100));   // 0, 1, 3 s failed; the 4 s pause runs
            time.Advance(TimeSpan.FromSeconds(1));
            Volatile.Write(ref back, true);
            var reboot = client.RebootAsync(RebootKind.Soft);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);               // fake time did not move: the pause was interrupted
            var device = await PendingVerifyAsync(connector);
            await device.SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.InRange((connector.AttemptTimes.Last() - lost).TotalSeconds, 4, 4.3);
            Assert.Equal(5, connector.Attempts);
        }
    }

    [Fact]
    public async Task RebootAsync_DuringAttempt_WaitsForItToEnd()
    {
        var policy = new RecordingPolicy();
        var (client, _, connector, rebooter, _, time) = await SeamAsync(o => o.RecoveryPolicy = policy);
        await using (client)
        {
            var attempt = await PendingVerifyAsync(connector);
            var reboot = client.RebootAsync(RebootKind.Soft);
            await Task.Delay(200);
            Assert.Empty(rebooter.Calls);                                                // the attempt runs on
            Assert.True(attempt.Client.IsConnected);
            attempt.CloseRemote();                                                       // it fails: the request runs instead of the pause
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);
            Assert.Empty(policy.Calls);                                                  // no policy call for that failure
            await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));   // the 1 s floor after the reboot
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
        }
    }

    [Fact]
    public async Task RebootAsync_AttemptSucceededMeanwhile_StillReboots()
    {
        var (client, _, connector, rebooter, logs, time) = await SeamAsync();
        await using (client)
        {
            var attempt = await PendingVerifyAsync(connector);
            var reboot = client.RebootAsync(RebootKind.Soft);
            await attempt.SendAsync(Resilient.Nak);                                      // published, then the request runs at once
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);
            await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));   // the 1 s floor after the reboot
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.Equal(2, logs.Events(1105).Count);
            Assert.Single(logs.Events(1103));                                            // the requested loss is not reported
        }
    }

    [Fact]
    public async Task RebootAsync_Coalesce_HardWins()
    {
        var (client, _, connector, rebooter, logs, time) = await SeamAsync();
        await using (client)
        {
            var attempt = await PendingVerifyAsync(connector);
            var soft = client.RebootAsync(RebootKind.Soft);
            var hard = client.RebootAsync(RebootKind.Hard);
            await attempt.SendAsync(Resilient.Nak);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);
            await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));   // the 1 s floor after the reboot
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await Task.WhenAll(soft, hard).WaitAsync(Limits.Test);
            Assert.Equal(new[] { RebootKind.Hard }, rebooter.Calls);
            Assert.Equal(2, logs.Events(1113).Count);
        }
    }

    // Spec 4.2 table row 4 (ruling C11): a manual request joins the reboot an escalation is running.
    [Fact]
    public async Task RebootAsync_DuringEscalationBootWait_Joins()
    {
        bool back = false;
        var policy = new RecordingPolicy { Decide = _ => RecoveryAction.Reboot(RebootKind.Soft) };
        var (client, _, connector, rebooter, logs, time) = await SeamAsync(o =>
        {
            o.RecoveryPolicy = policy;
            ((FakeRebooter)o.Rebooter!).BootTime = TimeSpan.FromSeconds(5);
        }, accept: n => n == 1 || Volatile.Read(ref back));
        await using (client)
        {
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);                 // the escalation's 5 s boot wait runs on fake time
            Volatile.Write(ref back, true);
            var reboot = client.RebootAsync(RebootKind.Hard);                           // joins, whatever its kind
            await Task.Delay(100);
            Assert.False(reboot.IsCompleted);
            await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            Assert.False(Assert.Single(rebooter.Contexts).Requested);
            Assert.Single(logs.Events(1113));
            Assert.Single(logs.Events(1114));
            Assert.Single(logs.Events(1116));
            Assert.Single(policy.Calls);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task RebootAsync_ManualCountsTowardsEscalation(int maxReboots, int expectedReboots)
    {
        var policy = new RecordingPolicy { Decide = new EscalatingRecoveryPolicy { MaxRebootsPerLoss = maxReboots }.OnAttemptFailed };
        var (client, _, _, rebooter, _, time) = await SeamAsync(o => o.RecoveryPolicy = policy, accept: n => n == 1);
        var reboot = client.RebootAsync(RebootKind.Hard);                                // noticed loss or not, the request runs before the first series
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == expectedReboots && policy.Calls.Count >= 6, TimeSpan.FromSeconds(1));   // six failures span about 31 s of backoff
        Assert.Equal(Enumerable.Repeat(RebootKind.Hard, expectedReboots), rebooter.Calls);
        Assert.All(policy.Calls, c => Assert.Equal(0, c.SoftReboots));
        Assert.Equal(1, policy.Calls.SkipWhile(c => c.HardReboots == 0).First().HardReboots);   // Ruling C17: the supervisor's first refusal may reach the policy before the request
        if (expectedReboots == 2) Assert.Equal(3, policy.Calls.Count(c => c.HardReboots == 1));   // three failures after the manual hard, then the second
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reboot.WaitAsync(Limits.Test));
    }

    // Review Focus 1.
    [Fact]
    public async Task RebootAsync_OnLastAllowedAttempt_RebootsThenGiveUpFailsTheCaller()
    {
        var (client, _, connector, rebooter, logs, time) = await SeamAsync(o => o.ReconnectAttempts = 2, accept: n => n != 2);
        await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));
        var attempt = await PendingVerifyAsync(connector);                               // attempt 2 of 2
        var reboot = client.RebootAsync(RebootKind.Soft);
        attempt.CloseRemote();
        await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);                     // the reboot still runs
        var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => reboot.WaitAsync(Limits.Test)));
        Assert.Equal(("attempts exhausted", "2"), (Assert.Single(logs.Events(1106)).Value("Reason"), logs.Events(1106)[0].Value("Attempts")));
        Assert.Equal(3, connector.Attempts);                                             // no attempt beyond the cap
        await client.DisposeAsync();
    }

    // Final F1 (reboot spec 4.2 row 1, 5.4): a manual request wakes the supervisor while the heartbeat waits for the
    // late reply of a device that does not answer; the reboot starts at once, and the end of the connection is no loss.
    [Fact]
    public async Task RebootAsync_HeartbeatUnanswered_RebootsAtOnce()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter { BootTime = TimeSpan.Zero });
        var connector = new PipeConnector(time);
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter);
        options.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Hex.Parse(Resilient.GetStatus), await device.ReadRequestAsync().WaitAsync(Limits.Test));   // the heartbeat, never answered
            time.Advance(TimeSpan.FromMilliseconds(200));                                // past ResponseTimeout, inside the late-reply deadline
            await Eventually.ThatAsync(() => logs.Events(1101).Count == 1);                 // HeartbeatMissed
            var reboot = client.RebootAsync(RebootKind.Soft);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);               // fake time did not reach the 750 ms deadline
            await time.AdvanceUntilAsync(() => connector.Attempts == 2, TimeSpan.FromMilliseconds(100));   // the 1 s floor after the reboot
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.True(client.IsConnected);
            Assert.Empty(logs.Events(1103));
            Assert.Single(logs.Events(1113));
            Assert.Single(logs.Events(1116));
        }
    }

    // A transport that closes the client it reboots the device of is application misuse, but it must not hold anything:
    // the disposal cancels the lifetime, which ends the supervisor's wait for the transport, so the disposal can finish.
    [Fact]
    public async Task RebootAsync_TransportDisposesTheClient_DoesNotDeadlock()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        {
            rebooter.OnReboot = (_, _, _) => client.DisposeAsync().AsTask();
            var caller = client.RebootAsync(RebootKind.Soft);
            await client.Completion.WaitAsync(Limits.Test);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.WaitAsync(Limits.Test));
            Assert.Empty(logs.Events(1106));
        }
    }

    // A transport that sends a command through the client waits for a reconnection that only the end of the reboot
    // step can start. The RebootTimeout ends that wait: the reboot fails, the client reconnects.
    [Fact]
    public async Task RebootAsync_TransportCallsTheClient_EndsByRebootTimeout()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs, o => o.RebootTimeout = TimeSpan.FromMilliseconds(300));
        await using (server)
        await using (client)
        {
            rebooter.OnReboot = (_, _, _) => client.GetAsync<InterfaceVersion>();
            await Assert.ThrowsAsync<TimeoutException>(() => client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test));
            await Eventually.ThatAsync(() => client.IsConnected, Limits.Test);
            Assert.Single(logs.Events(1115));
        }
    }

    // Final F6 (reboot spec 8): a logging provider that throws at the reboot events changes nothing. The policy lets
    // the first reconnection attempt fail (1104), then reboots (1114).
    [Fact]
    public async Task ThrowingProvider_ManualAndEscalatedRebootsComplete()
    {
        int decisions = 0;
        var policy = new RecordingPolicy { Decide = _ => Interlocked.Increment(ref decisions) == 1 ? RecoveryAction.Continue : RecoveryAction.Reboot(RebootKind.Soft) };
        var (server, client, rebooter) = await StartAsync(new FakeLoggerFactory(), o =>
        {
            (o.ResponseTimeout, o.LateReplyTimeout, o.RecoveryPolicy) = (TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), policy);
            o.LoggerFactory = new ControlClientLoggingTests.ThrowingLoggerFactory(1101, 1102, 1103, 1104, 1105, 1113, 1114, 1116);
        });
        await using (server)
        await using (client)
        {
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);
            Assert.True(client.IsConnected);
            server.Availability = ServerAvailability.Silent;
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 2 && client.IsConnected, Limits.Test);
            Assert.Equal(new[] { RebootKind.Soft, RebootKind.Soft }, rebooter.Calls);
            Assert.False(client.Completion.IsCompleted);
        }
    }

    // Final F4/F6: a provider that throws at 1118 does not end the series of the first connection.
    [Fact]
    public async Task ThrowingProvider_FirstConnectRetriesOn()
    {
        var time = new FakeTimeProvider();
        var connector = new PipeConnector(time) { Before = (n, _) => n < 3 ? Resilient.Refused() : Task.CompletedTask, Serve = d => d.NakEverythingAsync() };
        var options = Resilient.Seam(null, time);
        options.ConnectAttempts = 3;
        options.LoggerFactory = new ControlClientLoggingTests.ThrowingLoggerFactory(1118);
        var connecting = ResilientControlClient.ConnectAsync(connector.ConnectAsync, "pipe", options, CancellationToken.None);
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, TimeSpan.FromMilliseconds(100));
        await using var client = await connecting;
        Assert.Equal(3, connector.Attempts);
    }
}
