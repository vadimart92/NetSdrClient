using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientRebootTests
{
    private const string Outer = "NetSdr.Control.ResilientControlClient";

    /// <summary>A server, a client with a FakeRebooter that boots the server through CloseOnAccept for 200 ms, Fast options.</summary>
    private static async Task<(NetSdrTestServer Server, ResilientControlClient Client, FakeRebooter Rebooter)> StartAsync(
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

    [Test]
    public async Task RebootAsync_Connected_RestoresWithAfterReboot()
    {
        var logs = new FakeLoggerFactory();
        RebootKind? seen = null;
        var (server, client, rebooter) = await StartAsync(logs, o => o.ConnectionRestored = (ctx, _) => { seen = ctx.AfterReboot; return Task.CompletedTask; });
        await using (server)
        await using (client)
        {
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);
            Assert.That(client.IsConnected, Is.True);
            Assert.That(seen, Is.EqualTo(RebootKind.Soft));
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft }));
            var context = rebooter.Contexts.Sole();
            Assert.That(
                (context.Requested, context.LastRemoteEndPoint, context.Target),
                Is.EqualTo((true, client.RemoteEndPoint, $"127.0.0.1:{server.Port}")));
            Assert.That((logs.Events(1113).Sole().Level, logs.Events(1113)[0].Value("Kind")), Is.EqualTo((LogLevel.Information, "Soft")));
            Assert.That(logs.Events(1116), Has.Exactly(1).Items);
            Assert.That(logs.Events(1105), Has.Exactly(1).Items);
            Assert.That(logs.Events(1103), Is.Empty);
            Assert.That(logs.Events(1114), Is.Empty);
        }
    }

    [Test]
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
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft }));
            Assert.That(logs.Events(1113), Has.Count.EqualTo(2));
            Assert.That(logs.Events(1105), Has.Exactly(1).Items);
        }
    }

    [Test]
    public async Task RebootAsync_TransportFails_CallerGetsException_ClientReconnects()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        rebooter.OnReboot = (_, _, _) => Task.FromException(new ApplicationException("no service"));
        await using (server)
        await using (client)
        {
            await Assert.ThatAsync(() => client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test), Throws.TypeOf<ApplicationException>());
            Assert.That(logs.Events(1115).Sole().Exception, Is.TypeOf<ApplicationException>());
            await Eventually.ThatAsync(() => client.IsConnected && logs.Events(1105).Count == 1);
            Assert.That(logs.Events(1116), Is.Empty);
            Assert.That(logs.Events(1103), Is.Empty);
        }
    }

    [Test]
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
            await Assert.ThatAsync(() => reboot.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
            gate.SetResult();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft }));
            Assert.That(logs.Events(1116), Has.Exactly(1).Items);
        }
    }

    [Test]
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
            Assert.That((await set.WaitAsync(Limits.Test)).Level, Is.EqualTo(7));
            Assert.That(server.Received.Count(r => r.Code == AfGain.Code), Is.EqualTo(2));
            Assert.That(logs.Events(1100), Has.Exactly(1).Items);
        }
    }

    [Test]
    public async Task RebootAsync_WithoutRebooter_Throws()
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast());
        await using (server)
        await using (client)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => { _ = client.RebootAsync(RebootKind.Soft); });
            Assert.That(ex.Message, Is.EqualTo("No Rebooter is configured; set ResilientControlClientOptions.Rebooter."));
        }
    }

    [Test]
    public async Task RebootAsync_AfterDispose_Throws()
    {
        var (server, client, _) = await StartAsync(new FakeLoggerFactory());
        await using (server)
        {
            await client.DisposeAsync();
            Assert.That(() => { _ = client.RebootAsync(RebootKind.Soft); }, Throws.TypeOf<ObjectDisposedException>());
        }

        // After a give-up: the failure as the inner exception, like a command.
        var connector = new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam().WithRebooter(new FakeRebooter());
        options.ReconnectAttempts = 1;
        var (gaveUp, device) = await connector.StartAsync(options);
        device.CloseRemote();
        await Assert.ThatAsync(() => gaveUp.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        Assert.That(
            Assert.Throws<InvalidOperationException>(() => { _ = gaveUp.RebootAsync(RebootKind.Soft); }).InnerException,
            Is.TypeOf<IOException>());
        await gaveUp.DisposeAsync();
    }

    [Test]
    public async Task RebootAsync_InsideCallback_GivesUp()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        Exception? thrown = null;
        var started = await StartAsync(logs, o => o.ConnectionRestored = (_, _) =>
        {
            try
            { _ = client!.RebootAsync(RebootKind.Soft); }
            catch (InvalidOperationException e) { thrown = e; }
            return Task.CompletedTask;
        });
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await started.Server.DisconnectClientAsync();
            var failure = Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.That(failure.InnerException, Is.SameAs(thrown));
            Assert.That(started.Rebooter.Calls, Is.Empty);
        }

        Assert.That(logs.Events(1106).Sole().Value("Reason"), Is.EqualTo("ConnectionRestored called the ResilientControlClient"));
    }

    [Test]
    public async Task RebootAsync_PreCancelledToken_RegistersNothing()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        await using (client)
        {
            var reboot = client.RebootAsync(RebootKind.Soft, new CancellationToken(canceled: true));
            Assert.That(reboot.IsCanceled, Is.True);
            await Task.Delay(300);
            Assert.That(rebooter.Calls, Is.Empty);
            Assert.That(client.IsConnected, Is.True);
            Assert.That(logs.Events(1113), Is.Empty);
        }
    }

    // Review Focus 4.
    [Test]
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
            Assert.That(rebooter.Calls, Has.Exactly(1).Items);
            Assert.That(logs.Events(1116), Has.Exactly(1).Items);
            Assert.That(logs.Events(1113), Has.Count.EqualTo(16));
            Assert.That(client.IsConnected, Is.True);
        }
    }

    // Review Focus 3.
    [Test]
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
            Assert.That(client.IsConnected, Is.False);                                 // no lock is held by it
            var command = client.GetAsync<InterfaceVersion>();
            var reboot = client.RebootAsync(RebootKind.Soft);                          // registers at once
            Assert.That(command.IsCompleted || reboot.IsCompleted, Is.False);
            Volatile.Write(ref back, true);
            gate.Set();
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1 && logs.Events(1105).Count == 1, TimeSpan.FromMilliseconds(100));
            await Assert.ThatAsync(
                () => command.WaitAsync(Limits.Test),
                Throws.TypeOf<NetSdrNakException>());   // the pipe NAKs it after the restore
            await reboot.WaitAsync(Limits.Test);
        }
    }

    [Test]
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
            await Assert.ThatAsync(
                () => client.RebootAsync(RebootKind.Hard).WaitAsync(Limits.Test),
                Throws.TypeOf<ApplicationException>());   // 1113, 1115
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 2);
            rebooter.OnReboot = booting;
            server.Availability = ServerAvailability.Silent;                                            // 1102, 1103, then 1117, 1114, 1116
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 3);
        }

        void Expect(int id, int count, LogLevel level)
        {
            Assert.That(logs.Events(id), Has.Count.EqualTo(count));
            Assert.That(logs.Events(id).Select(r => (r.Level, r.Category)), Has.All.EqualTo((level, Outer)));
        }

        Expect(1113, 2, LogLevel.Information);
        Expect(1114, 1, LogLevel.Warning);
        Expect(1115, 1, LogLevel.Warning);
        Expect(1116, 2, LogLevel.Information);
        Expect(1117, 1, LogLevel.Warning);
        Assert.That(
            (logs.Events(1114)[0].Value("Kind"), logs.Events(1114)[0].Value("FailedAttempts"), logs.Events(1114)[0].Value("Phase")),
            Is.EqualTo(("Soft", "2", "Verify")));
        Assert.That(logs.Events(1106), Is.Empty);
    }

    /// <summary>A seam on fake time whose attempt 1 is answered by StartAsync; later verifications the test answers itself.</summary>
    private static async Task<(ResilientControlClient Client, PipeDevice Device, PipeConnector Connector, FakeRebooter Rebooter, FakeLoggerFactory Logs, FakeTimeProvider Time)>
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

    private static async Task<PipeDevice> PendingVerifyAsync(PipeConnector connector)
    {
        PipeDevice device = await connector.NextAsync();
        Assert.That(await device.ReadRequestAsync(), Is.EqualTo(Hex.Parse(Resilient.GetStatus)));
        return device;
    }

    [Test]
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
            Assert.That((connector.AttemptTimes.Last() - lost).TotalSeconds, Is.InRange(4, 4.3));
            Assert.That(connector.Attempts, Is.EqualTo(5));
        }
    }

    [Test]
    public async Task RebootAsync_DuringAttempt_WaitsForItToEnd()
    {
        var policy = new RecordingPolicy();
        var (client, _, connector, rebooter, _, time) = await SeamAsync(o => o.RecoveryPolicy = policy);
        await using (client)
        {
            var attempt = await PendingVerifyAsync(connector);
            var reboot = client.RebootAsync(RebootKind.Soft);
            await Task.Delay(200);
            Assert.That(rebooter.Calls, Is.Empty);                                       // the attempt runs on
            Assert.That(attempt.Client.IsConnected, Is.True);
            attempt.CloseRemote();                                                       // it fails: the request runs instead of the pause
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);
            Assert.That(policy.Calls, Is.Empty);                                         // no policy call for that failure
            await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));   // the 1 s floor after the reboot
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
        }
    }

    [Test]
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
            Assert.That(logs.Events(1105), Has.Count.EqualTo(2));
            Assert.That(
                logs.Events(1103),
                Has.Exactly(1).Items);                                            // the requested loss is not reported
        }
    }

    [Test]
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
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Hard }));
            Assert.That(logs.Events(1113), Has.Count.EqualTo(2));
        }
    }

    // Spec 4.2 table row 4 (ruling C11): a manual request joins the reboot an escalation is running.
    [Test]
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
            Assert.That(reboot.IsCompleted, Is.False);
            await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft }));
            Assert.That(rebooter.Contexts.Sole().Requested, Is.False);
            Assert.That(logs.Events(1113), Has.Exactly(1).Items);
            Assert.That(logs.Events(1114), Has.Exactly(1).Items);
            Assert.That(logs.Events(1116), Has.Exactly(1).Items);
            Assert.That(policy.Calls, Has.Exactly(1).Items);
        }
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    public async Task RebootAsync_ManualCountsTowardsEscalation(int maxReboots, int expectedReboots)
    {
        var policy = new RecordingPolicy { Decide = new EscalatingRecoveryPolicy { MaxRebootsPerLoss = maxReboots }.OnAttemptFailed };
        var (client, _, _, rebooter, _, time) = await SeamAsync(o => o.RecoveryPolicy = policy, accept: n => n == 1);
        var reboot = client.RebootAsync(RebootKind.Hard);                                // noticed loss or not, the request runs before the first series
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == expectedReboots && policy.Calls.Count >= 6, TimeSpan.FromSeconds(1));   // six failures span about 31 s of backoff
        Assert.That(rebooter.Calls, Is.EqualTo(Enumerable.Repeat(RebootKind.Hard, expectedReboots)));
        Assert.That(policy.Calls.Select(c => c.SoftReboots), Has.All.EqualTo(0));
        Assert.That(
            policy.Calls.SkipWhile(c => c.HardReboots == 0).First().HardReboots,
            Is.EqualTo(1));   // Ruling C17: the supervisor's first refusal may reach the policy before the request
        if (expectedReboots == 2)
            Assert.That(
                policy.Calls.Count(c => c.HardReboots == 1),
                Is.EqualTo(3));   // three failures after the manual hard, then the second
        await client.DisposeAsync();
        await Assert.ThatAsync(() => reboot.WaitAsync(Limits.Test), Throws.TypeOf<ObjectDisposedException>());
    }

    // Review Focus 1.
    [Test]
    public async Task RebootAsync_OnLastAllowedAttempt_RebootsThenGiveUpFailsTheCaller()
    {
        var (client, _, connector, rebooter, logs, time) = await SeamAsync(o => o.ReconnectAttempts = 2, accept: n => n != 2);
        await time.AdvanceUntilAsync(() => connector.Attempts == 3, TimeSpan.FromMilliseconds(100));
        var attempt = await PendingVerifyAsync(connector);                               // attempt 2 of 2
        var reboot = client.RebootAsync(RebootKind.Soft);
        attempt.CloseRemote();
        await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);                     // the reboot still runs
        var failure = Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.That(Assert.ThrowsAsync<IOException>(() => reboot.WaitAsync(Limits.Test)), Is.SameAs(failure));
        Assert.That(
            (logs.Events(1106).Sole().Value("Reason"), logs.Events(1106)[0].Value("Attempts")),
            Is.EqualTo(("attempts exhausted", "2")));
        Assert.That(connector.Attempts, Is.EqualTo(3));                                             // no attempt beyond the cap
        await client.DisposeAsync();
    }

    // Final F1 (reboot spec 4.2 row 1, 5.4): a manual request wakes the supervisor while the heartbeat waits for the
    // late reply of a device that does not answer; the reboot starts at once, and the end of the connection is no loss.
    [Test]
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
            Assert.That(
                await device.ReadRequestAsync().WaitAsync(Limits.Test),
                Is.EqualTo(Hex.Parse(Resilient.GetStatus)));   // the heartbeat, never answered
            time.Advance(TimeSpan.FromMilliseconds(200));                                // past ResponseTimeout, inside the late-reply deadline
            await Eventually.ThatAsync(() => logs.Events(1101).Count == 1);                 // HeartbeatMissed
            var reboot = client.RebootAsync(RebootKind.Soft);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);               // fake time did not reach the 750 ms deadline
            await time.AdvanceUntilAsync(() => connector.Attempts == 2, TimeSpan.FromMilliseconds(100));   // the 1 s floor after the reboot
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.That(client.IsConnected, Is.True);
            Assert.That(logs.Events(1103), Is.Empty);
            Assert.That(logs.Events(1113), Has.Exactly(1).Items);
            Assert.That(logs.Events(1116), Has.Exactly(1).Items);
        }
    }

    // A transport that closes the client it reboots the device of is application misuse, but it must not hold anything:
    // the disposal cancels the lifetime, which ends the supervisor's wait for the transport, so the disposal can finish.
    [Test]
    public async Task RebootAsync_TransportDisposesTheClient_DoesNotDeadlock()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        {
            rebooter.OnReboot = (_, _, _) => client.DisposeAsync().AsTask();
            var caller = client.RebootAsync(RebootKind.Soft);
            await client.Completion.WaitAsync(Limits.Test);
            await Assert.ThatAsync(() => caller.WaitAsync(Limits.Test), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(logs.Events(1106), Is.Empty);
        }
    }

    // A transport that sends a command through the client waits for a reconnection that only the end of the reboot
    // step can start. The RebootTimeout ends that wait: the reboot fails, the client reconnects.
    [Test]
    public async Task RebootAsync_TransportCallsTheClient_EndsByRebootTimeout()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs, o => o.RebootTimeout = TimeSpan.FromMilliseconds(300));
        await using (server)
        await using (client)
        {
            rebooter.OnReboot = (_, _, _) => client.GetAsync<InterfaceVersion>();
            await Assert.ThatAsync(() => client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
            await Eventually.ThatAsync(() => client.IsConnected, Limits.Test);
            Assert.That(logs.Events(1115), Has.Exactly(1).Items);
        }
    }
}
