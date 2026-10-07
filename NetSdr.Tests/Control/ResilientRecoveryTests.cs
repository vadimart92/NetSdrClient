using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientRecoveryTests
{
    // A seam that accepts attempt 1 and refuses every later one until `back` says otherwise.
    static PipeConnector Failing(FakeTimeProvider time, Func<bool>? back = null) => new(time)
    {
        Before = (n, _) => n == 1 || back?.Invoke() == true ? Task.CompletedTask : Resilient.Refused(),
        Serve = d => d.NakEverythingAsync(),
    };

    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    // The prologue of NoRebooter_PolicyNeverCalled with a rebooter: connected, then lost one second later.
    static async Task<(ResilientControlClient Client, PipeDevice Device, PipeConnector Connector)> Start(
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

    [Fact]
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
            Assert.Empty(policy.Calls);
        }
    }

    [Fact]
    public async Task FakeRebooter_RecordsCallsAndBootTime()
    {
        var rebooter = new FakeRebooter { BootTimeOf = k => k == RebootKind.Hard ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(1) };
        await rebooter.RebootAsync(RebootKind.Hard, new RebootContext("pipe", null, false), default);
        Assert.Equal(new[] { RebootKind.Hard }, rebooter.Calls);
        Assert.Equal((4, 1), ((int)rebooter.GetBootTime(RebootKind.Hard).TotalSeconds, (int)rebooter.GetBootTime(RebootKind.Soft).TotalSeconds));
        Assert.False(Assert.Single(rebooter.Contexts).Requested);
    }

    [Fact]
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
            for (int i = 0; i < 6; i++) Assert.InRange(starts[i], expected[i], expected[i] + 0.3);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            var escalated = Assert.Single(logs.Events(1114));
            Assert.Equal((LogLevel.Warning, "Soft", "3", "Connect"), (escalated.Level, escalated.Value("Kind"), escalated.Value("FailedAttempts"), escalated.Value("Phase")));
            Assert.IsType<SocketException>(escalated.Exception);
            Assert.Equal((LogLevel.Information, "pipe", TimeSpan.FromSeconds(2)), (logs.Events(1116)[0].Level, logs.Events(1116)[0].Value("Target"), logs.Events(1116)[0].Span("BootTime")));
            Assert.Equal(new[] { 1.0, 2, 1, 2 }, logs.Events(1104).Take(4).Select(r => r.Span("Delay").TotalSeconds));
            Assert.False(Assert.Single(rebooter.Contexts).Requested);
        }
    }

    [Fact]
    public async Task Escalation_HardWhenSoftDidNotHelp()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => connector.Attempts >= 12, Step);
            Assert.Equal(new[] { RebootKind.Soft, RebootKind.Hard }, rebooter.Calls);  // after hard only plain attempts
            Assert.Equal(new[] { "Soft", "Hard" }, logs.Events(1114).Select(r => r.Value("Kind")));
        }
    }

    [Fact]
    public async Task Escalation_SucceedsAfterSoft_RestoredSeesAfterReboot()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        RebootKind? seen = null;
        var (client, _, connector) = await Start(logs, time, rebooter, back: () => rebooter.Calls.Count > 0,
            restore: (ctx, _) => { seen = ctx.AfterReboot; return Task.CompletedTask; });
        await using (client)
        {
            await time.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, Step);
            Assert.Equal(RebootKind.Soft, seen);
            Assert.Equal("4", logs.Events(1105)[0].Value("Attempts"));
            Assert.True(client.IsConnected);
        }
    }

    [Fact]
    public async Task RebooterThrows_1115_CountedAndContinues()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (k, _, _) => k == RebootKind.Soft ? throw new ApplicationException("no service") : Task.CompletedTask;
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2 && logs.Events(1116).Count == 1, Step);   // Ruling C6
            Assert.Equal(new[] { RebootKind.Soft, RebootKind.Hard }, rebooter.Calls);
            Assert.IsType<ApplicationException>(Assert.Single(logs.Events(1115)).Exception);
            Assert.Equal("Hard", Assert.Single(logs.Events(1116)).Value("Kind"));   // no 1116 and no boot wait for the failed soft
            Assert.InRange((connector.AttemptTimes.ElementAt(4) - connector.AttemptTimes.ElementAt(3)).TotalSeconds, 1, 1.3);   // attempt 4 right after the floor
        }
    }

    [Fact]
    public async Task RebooterTimeout_TimeoutException()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (_, _, ct) => Task.Delay(Timeout.Infinite, ct);
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.RebootTimeout = TimeSpan.FromMilliseconds(500));
        await using (client)
        {
            await time.AdvanceUntilAsync(() => logs.Events(1115).Count == 1, Step);
            var timeout = Assert.IsType<TimeoutException>(logs.Events(1115)[0].Exception);
            Assert.StartsWith("The Soft reboot of pipe did not complete within", timeout.Message);
            await time.AdvanceUntilAsync(() => connector.Attempts >= 5, Step);        // attempts go on
        }
    }

    // Review Focus 2.
    [Fact]
    public async Task RebooterIgnoresToken_TimeoutAndDisposeStillEnd()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (_, _, _) => new TaskCompletionSource().Task;            // never completes, ignores ct
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.RebootTimeout = TimeSpan.FromMilliseconds(500));
        await time.AdvanceUntilAsync(() => logs.Events(1115).Count == 1, Step);
        Assert.IsType<TimeoutException>(logs.Events(1115)[0].Exception);
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2, Step);        // the hard reboot hangs now
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        Assert.True(client.Completion.IsCompletedSuccessfully);
        Assert.Empty(logs.Events(1106));
    }

    [Fact]
    public Task BootTimeNegative_TreatedAsFailure() => BootTimeIsFailureAsync(-1);

    // Review Focus 5.
    [Fact]
    public Task BootTimeAboveInt32Milliseconds_TreatedAsFailure() => BootTimeIsFailureAsync(int.MaxValue + 1L);

    async Task BootTimeIsFailureAsync(long milliseconds)
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.BootTimeOf = _ => TimeSpan.FromMilliseconds(milliseconds);
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2 && logs.Events(1115).Count == 2, Step);   // Ruling C6
            Assert.Equal(2, logs.Events(1115).Count);
            Assert.All(logs.Events(1115), r => Assert.IsType<InvalidOperationException>(r.Exception));
            Assert.Empty(logs.Events(1116));
            Assert.Empty(logs.Events(1106));
        }
    }

    [Fact]
    public async Task PolicyThrows_1117_Continue()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = _ => throw new ApplicationException("policy") };
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => connector.Attempts >= 5 && logs.Events(1117).Count >= 4, Step);   // Ruling C6
            Assert.Equal(4, logs.Events(1117).Count);
            Assert.All(logs.Events(1117), r => Assert.Equal(LogLevel.Warning, r.Level));
            Assert.Empty(rebooter.Calls);
            Assert.Equal(new[] { 1.0, 2, 4 }, logs.Events(1104).Take(3).Select(r => r.Span("Delay").TotalSeconds));
        }
    }

    [Fact]
    public async Task PolicyGivesUp_1106_CompletionFaults()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = c => c.FailedAttempts == 2 ? RecoveryAction.GiveUp : RecoveryAction.Continue };
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, Step);
        var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.IsType<SocketException>(failure.InnerException);
        Assert.Contains("after 2 attempt(s)", failure.Message);
        Assert.Equal((LogLevel.Error, "recovery policy gave up", "2"), (logs.Events(1106)[0].Level, logs.Events(1106)[0].Value("Reason"), logs.Events(1106)[0].Value("Attempts")));
        await client.DisposeAsync();
    }

    [Fact]
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
            Assert.Equal((1, 1, ReconnectPhase.Connect, 0, 0), (calls[0].FailedAttempts, calls[0].FailedAttemptsSinceReboot, calls[0].Phase, calls[0].SoftReboots, calls[0].HardReboots));
            Assert.IsType<SocketException>(calls[0].Failure);
            Assert.True(calls[3].Downtime > calls[0].Downtime && calls[3].Downtime >= TimeSpan.FromSeconds(7));
            Assert.Equal((4, 4), (calls[3].FailedAttempts, calls[3].FailedAttemptsSinceReboot));
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
                Assert.Equal(phase, p.Calls.First().Phase);
            }
        }
    }

    [Fact]
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
            Assert.Equal((1, 1, 0, 0), (policy.Calls.First().FailedAttempts, policy.Calls.First().FailedAttemptsSinceReboot, policy.Calls.First().SoftReboots, policy.Calls.First().HardReboots));
        }
    }

    [Fact]
    public async Task ReconnectAttempts_CapsAcrossReboots()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.ReconnectAttempts = 5);
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, Step);
        Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
        Assert.Equal(5, connector.Attempts - 1);
        Assert.Equal(("attempts exhausted", "5"), (logs.Events(1106)[0].Value("Reason"), logs.Events(1106)[0].Value("Attempts")));
        Assert.Equal(3, logs.Events(1104).Count);                                   // attempts 1, 2 and 4; 3 ends in 1114, 5 in 1106
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DuringBootWait_ReturnsWithout1106()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter);
        await time.AdvanceUntilAsync(() => logs.Events(1116).Count == 1, Step);
        int attempts = connector.Attempts;
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);                // fake time stands still inside the 2 s wait
        Assert.True(client.Completion.IsCompletedSuccessfully);
        Assert.Equal(attempts, connector.Attempts);
        Assert.Empty(logs.Events(1106));
    }
}
