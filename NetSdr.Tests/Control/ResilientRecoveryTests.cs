using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientRecoveryTests
{
    // A seam that accepts attempt 1 and refuses every later one until `back` says otherwise.
    private static PipeConnector Failing(FakeTimeProvider time, Func<bool>? back = null) => new(time)
    {
        Before = (n, _) => n == 1 || back?.Invoke() == true ? Task.CompletedTask : Resilient.Refused(),
        Serve = d => d.NakEverythingAsync(),
    };

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    // The prologue of NoRebooter_PolicyNeverCalled with a rebooter: connected, then lost one second later.
    private static async Task<(ResilientControlClient Client, PipeDevice Device, PipeConnector Connector)> Start(
        FakeLoggerFactory logs, FakeTimeProvider time, FakeRebooter rebooter, IRecoveryPolicy? policy = null, Func<bool>? back = null,
        Func<ConnectionRestoredContext, CancellationToken, Task>? restore = null, Action<ResilientControlClientOptions>? configure = null)
    {
        var connector = Failing(time, back);
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter, policy);
        options.ConnectionRestored = restore;
        configure?.Invoke(options);
        var (client, device) = await connector.StartAsync(options);
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        return (client, device, connector);
    }

    [Test]
    public async Task NoRebooter_PolicyNeverCalled()
    {
        var (logs, time, policy) = (new FakeLoggerFactory(), new FakeTimeProvider(), new RecordingPolicy());
        var connector = Failing(time);
        var options = Resilient.Seam(logs, time);
        options.RecoveryPolicy = policy;                                   // allowed without a Rebooter, never called
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            device.CloseRemote();
            await time.AdvanceUntilAsync(() => logs.Events(1104).Count >= 5, TimeSpan.FromMilliseconds(500));
            Assert.That(policy.Calls, Is.Empty);
        }
    }

    [Test]
    public async Task FakeRebooter_RecordsCallsAndBootTime()
    {
        var rebooter = new FakeRebooter { BootTimeOf = k => k == RebootKind.Hard ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(1) };
        await rebooter.RebootAsync(RebootKind.Hard, new RebootContext("pipe", null, false), default);
        Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Hard }));
        Assert.That(
            ((int)rebooter.GetBootTime(RebootKind.Hard).TotalSeconds, (int)rebooter.GetBootTime(RebootKind.Soft).TotalSeconds),
            Is.EqualTo((4, 1)));
        Assert.That(rebooter.Contexts.Sole().Requested, Is.False);
    }

    [Test]
    public async Task Escalation_SoftAfterThree_BootWait_BackoffRestarts()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, device, connector) = await Start(logs, time, rebooter, new EscalatingRecoveryPolicy { MaxRebootsPerLoss = 1 });   // Ruling C3
        await using (client)
        {
            DateTimeOffset lost = time.GetUtcNow();
            // Attempt 1 and its 1104 run in real time on pool threads: fake time moves once its 1 s backoff exists (Ruling C6).
            await Eventually.ThatAsync(() => logs.Events(1104).Count == 1);
            await time.AdvanceUntilAsync(() => connector.Attempts >= 7, Step);
            double[] starts = connector.AttemptTimes.Skip(1).Take(6).Select(t => (t - lost).TotalSeconds).ToArray();
            double[] expected = [0, 1, 3, 5, 6, 8];                           // soft at 3 s, bootTime 2 s, then 1, 2 s again
            for (int i = 0; i < 6; i++)
                Assert.That(starts[i], Is.InRange(expected[i], expected[i] + 0.3));
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft }));
            var escalated = logs.Events(1114).Sole();
            Assert.That(
                (escalated.Level, escalated.Value("Kind"), escalated.Value("FailedAttempts"), escalated.Value("Phase")),
                Is.EqualTo((LogLevel.Warning, "Soft", "3", "Connect")));
            Assert.That(escalated.Exception, Is.TypeOf<SocketException>());
            Assert.That(
                (logs.Events(1116)[0].Level, logs.Events(1116)[0].Value("Target"), logs.Events(1116)[0].Span("BootTime")),
                Is.EqualTo((LogLevel.Information, "pipe", TimeSpan.FromSeconds(2))));
            Assert.That(logs.Events(1104).Take(4).Select(r => r.Span("Delay").TotalSeconds), Is.EqualTo(new[] { 1.0, 2, 1, 2 }));
            Assert.That(rebooter.Contexts.Sole().Requested, Is.False);
        }
    }

    [Test]
    public async Task Escalation_HardWhenSoftDidNotHelp()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => connector.Attempts >= 12, Step);
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft, RebootKind.Hard }));   // after hard only plain attempts
            Assert.That(logs.Events(1114).Select(r => r.Value("Kind")), Is.EqualTo(new[] { "Soft", "Hard" }));
        }
    }

    [Test]
    public async Task Escalation_SucceedsAfterSoft_RestoredSeesAfterReboot()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        RebootKind? seen = null;
        var (client, _, connector) = await Start(logs, time, rebooter, back: () => rebooter.Calls.Count > 0,
            restore: (ctx, _) => { seen = ctx.AfterReboot; return Task.CompletedTask; });
        await using (client)
        {
            await time.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, Step);
            Assert.That(seen, Is.EqualTo(RebootKind.Soft));
            Assert.That(logs.Events(1105)[0].Value("Attempts"), Is.EqualTo("4"));
            Assert.That(client.IsConnected, Is.True);
        }
    }

    [Test]
    public async Task RebooterThrows_1115_CountedAndContinues()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (k, _, _) => k == RebootKind.Soft ? throw new ApplicationException("no service") : Task.CompletedTask;
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2 && logs.Events(1116).Count == 1, Step);   // Ruling C6
            Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft, RebootKind.Hard }));
            Assert.That(logs.Events(1115).Sole().Exception, Is.TypeOf<ApplicationException>());
            Assert.That(logs.Events(1116).Sole().Value("Kind"), Is.EqualTo("Hard"));   // no 1116 and no boot wait for the failed soft
            Assert.That(
                (connector.AttemptTimes.ElementAt(4) - connector.AttemptTimes.ElementAt(3)).TotalSeconds,
                Is.InRange(1, 1.3));   // attempt 4 right after the floor
        }
    }

    [Test]
    public async Task RebooterTimeout_TimeoutException()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (_, _, ct) => Task.Delay(Timeout.Infinite, ct);
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.RebootTimeout = TimeSpan.FromMilliseconds(500));
        await using (client)
        {
            await time.AdvanceUntilAsync(() => logs.Events(1115).Count == 1, Step);
            Assert.That(
                logs.Events(1115)[0].Exception,
                Is.TypeOf<TimeoutException>().With.Message.StartsWith("The Soft reboot of pipe did not complete within"));
            await time.AdvanceUntilAsync(() => connector.Attempts >= 5, Step);        // attempts go on
        }
    }

    // Review Focus 2.
    [Test]
    public async Task RebooterIgnoresToken_TimeoutAndDisposeStillEnd()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (_, _, _) => new TaskCompletionSource().Task;            // never completes, ignores ct
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.RebootTimeout = TimeSpan.FromMilliseconds(500));
        await time.AdvanceUntilAsync(() => logs.Events(1115).Count == 1, Step);
        Assert.That(logs.Events(1115)[0].Exception, Is.TypeOf<TimeoutException>());
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2, Step);        // the hard reboot hangs now
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(client.Completion.IsCompletedSuccessfully, Is.True);
        Assert.That(logs.Events(1106), Is.Empty);
    }

    [Test]
    public Task BootTimeNegative_TreatedAsFailure() => BootTimeIsFailureAsync(-1);

    // Review Focus 5.
    [Test]
    public Task BootTimeAboveInt32Milliseconds_TreatedAsFailure() => BootTimeIsFailureAsync(int.MaxValue + 1L);

    private async Task BootTimeIsFailureAsync(long milliseconds)
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.BootTimeOf = _ => TimeSpan.FromMilliseconds(milliseconds);
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2 && logs.Events(1115).Count == 2, Step);   // Ruling C6
            Assert.That(logs.Events(1115), Has.Count.EqualTo(2));
            Assert.That(logs.Events(1115).Select(r => r.Exception), Has.All.TypeOf<InvalidOperationException>());
            Assert.That(logs.Events(1116), Is.Empty);
            Assert.That(logs.Events(1106), Is.Empty);
        }
    }

    [Test]
    public async Task PolicyThrows_1117_Continue()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = _ => throw new ApplicationException("policy") };
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => connector.Attempts >= 5 && logs.Events(1117).Count >= 4, Step);   // Ruling C6
            Assert.That(logs.Events(1117), Has.Count.EqualTo(4));
            Assert.That(logs.Events(1117).Select(r => r.Level), Has.All.EqualTo(LogLevel.Warning));
            Assert.That(rebooter.Calls, Is.Empty);
            Assert.That(logs.Events(1104).Take(3).Select(r => r.Span("Delay").TotalSeconds), Is.EqualTo(new[] { 1.0, 2, 4 }));
        }
    }

    [Test]
    public async Task PolicyGivesUp_1106_CompletionFaults()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = c => c.FailedAttempts == 2 ? RecoveryAction.GiveUp : RecoveryAction.Continue };
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, Step);
        var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.That(failure.InnerException, Is.TypeOf<SocketException>());
        Assert.That(failure.Message, Does.Contain("after 2 attempt(s)"));
        Assert.That(
            (logs.Events(1106)[0].Level, logs.Events(1106)[0].Value("Reason"), logs.Events(1106)[0].Value("Attempts")),
            Is.EqualTo((LogLevel.Error, "recovery policy gave up", "2")));
        await client.DisposeAsync();
    }

    [Test]
    public async Task RecoveryContext_Fields()
    {
        // Connect: the seam refuses. Downtime grows with fake time, the counters start at zero.
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy();
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => policy.Calls.Count >= 4, Step);
            var calls = policy.Calls.ToArray();
            Assert.That(
                (calls[0].FailedAttempts, calls[0].FailedAttemptsSinceReboot, calls[0].Phase, calls[0].SoftReboots, calls[0].HardReboots),
                Is.EqualTo((1, 1, ReconnectPhase.Connect, 0, 0)));
            Assert.That(calls[0].Failure, Is.TypeOf<SocketException>());
            Assert.That(calls[3].Downtime > calls[0].Downtime && calls[3].Downtime >= TimeSpan.FromSeconds(7), Is.True);
            Assert.That((calls[3].FailedAttempts, calls[3].FailedAttemptsSinceReboot), Is.EqualTo((4, 4)));
        }

        // Verify: a device that accepts and never answers. Restore: a callback that throws.
        foreach (var (phase, serve, restore) in new (ReconnectPhase, Func<PipeDevice, Task>?, Func<ConnectionRestoredContext, CancellationToken, Task>?)[]
        {
            (ReconnectPhase.Verify, null, null),                                   // Ruling C4: the first verification answered, later ones not
            (ReconnectPhase.Restore, d => d.NakEverythingAsync(), (_, _) => throw new ApplicationException("restore")),
        })
        {
            var (l, t, r, p) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter(), new RecordingPolicy());
            var connector2 = new PipeConnector(t) { Serve = serve };
            var options = Resilient.Seam(l, t).WithRebooter(r, p);
            options.ConnectionRestored = restore;
            var (c, d) = await connector2.StartAsync(options);     // StartAsync answers the first verification itself when Serve is null
            await using (c)
            {
                t.Advance(TimeSpan.FromSeconds(1));
                d.CloseRemote();
                await t.AdvanceUntilAsync(() => p.Calls.Count >= 1, Step);
                Assert.That(p.Calls.First().Phase, Is.EqualTo(phase));
            }
        }
    }

    [Test]
    public async Task CountersResetForNewLoss()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = new EscalatingRecoveryPolicy().OnAttemptFailed };
        bool back = false;
        var (client, _, connector) = await Start(logs, time, rebooter, policy, back: () => Volatile.Read(ref back));
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1, Step);
            Volatile.Write(ref back, true);
            await time.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, Step);
            PipeDevice latest = await connector.NextAsync();                    // Ruling C5: refused attempts attach no device
            Volatile.Write(ref back, false);
            policy.Calls.Clear();
            time.Advance(TimeSpan.FromSeconds(1));
            latest.CloseRemote();
            await time.AdvanceUntilAsync(() => policy.Calls.Count >= 1, Step);
            Assert.That(
                (policy.Calls.First().FailedAttempts, policy.Calls.First().FailedAttemptsSinceReboot, policy.Calls.First().SoftReboots, policy.Calls.First().HardReboots),
                Is.EqualTo((1, 1, 0, 0)));
        }
    }

    [Test]
    public async Task ReconnectAttempts_CapsAcrossReboots()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.ReconnectAttempts = 5);
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, Step);
        Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft }));
        Assert.That(connector.Attempts - 1, Is.EqualTo(5));
        Assert.That(
            (logs.Events(1106)[0].Value("Reason"), logs.Events(1106)[0].Value("Attempts")),
            Is.EqualTo(("attempts exhausted", "5")));
        Assert.That(
            logs.Events(1104).Count,
            Is.EqualTo(3));                                   // attempts 1, 2 and 4; 3 ends in 1114, 5 in 1106
        await client.DisposeAsync();
    }

    [Test]
    public async Task Dispose_DuringBootWait_ReturnsWithout1106()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter);
        await time.AdvanceUntilAsync(() => logs.Events(1116).Count == 1, Step);
        int attempts = connector.Attempts;
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);                // fake time stands still inside the 2 s wait
        Assert.That(client.Completion.IsCompletedSuccessfully, Is.True);
        Assert.That(connector.Attempts, Is.EqualTo(attempts));
        Assert.That(logs.Events(1106), Is.Empty);
    }
}
