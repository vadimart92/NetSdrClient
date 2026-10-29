using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientFirstConnectTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    private static Task<ResilientControlClient> Connect(PipeConnector connector, ResilientControlClientOptions options, CancellationToken ct = default) =>
        ResilientControlClient.ConnectAsync(connector.ConnectAsync, "pipe", options, ct);

    [TestCase(0)]
    [TestCase(-1)]
    public void InvalidConnectAttempts_Throws(int attempts)
    {
        var options = new ResilientControlClientOptions { ConnectAttempts = attempts };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), options); });
        Assert.That(ex.Message, Does.Contain("ConnectAttempts must be at least 1."));
    }

    [Test]
    public async Task RecoveryPolicyWithoutRebooter_IsAllowed()
    {
        var options = new ResilientControlClientOptions { RecoveryPolicy = new EscalatingRecoveryPolicy() };   // validated, never called
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThatAsync(
            () => ResilientControlClient.ConnectAsync(refused.ConnectAsync, "pipe", options, default).WaitAsync(Limits.Test),
            Throws.TypeOf<SocketException>());
    }

    [Test]
    public async Task NoRebooter_DefaultIsOneAttempt()
    {
        var logs = new FakeLoggerFactory();
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThatAsync(() => Connect(refused, Resilient.Seam(logs)).WaitAsync(Limits.Test), Throws.TypeOf<SocketException>());
        Assert.That(refused.Attempts, Is.EqualTo(1));
        Assert.That(logs.Events(1118), Is.Empty);
    }

    [Test]
    public async Task ExplicitConnectAttempts_RetriesWithBackoff()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var options = Resilient.Seam(logs, time);
        options.ConnectAttempts = 3;
        DateTimeOffset start = time.GetUtcNow();
        var connecting = Connect(refused, options);
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThatAsync(() => connecting, Throws.TypeOf<SocketException>());
        double[] starts = refused.AttemptTimes.Select(t => (t - start).TotalSeconds).ToArray();
        double[] expected = [0, 1, 3];
        Assert.That(starts, Has.Length.EqualTo(3));
        for (int i = 0; i < 3; i++)
            Assert.That(starts[i], Is.InRange(expected[i], expected[i] + 0.3));
        Assert.That(
            logs.Events(1118).Select(r => (r.Value("Attempt"), r.Value("Attempts"), r.Value("Phase"), r.Span("Delay").TotalSeconds)),
            Is.EqualTo(new (string?, string?, string?, double)[] { ("1", "3", "Connect", 1.0), ("2", "3", "Connect", 2.0) }));
        Assert.That(logs.Events(1118).Select(r => (r.Level, r.Value("Target"))), Has.All.EqualTo((LogLevel.Warning, "pipe")));
        Assert.That(logs.Events(1104), Is.Empty);
    }

    [Test]
    public async Task Rebooter_DefaultEightAttempts_FullLadder()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThatAsync(() => connecting, Throws.TypeOf<SocketException>());
        Assert.That(refused.Attempts, Is.EqualTo(8));
        Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft, RebootKind.Hard }));
        Assert.That(
            logs.Events(1118).Count,
            Is.EqualTo(5));                                        // attempts 1, 2, 4, 5, 7; 3 and 6 end in 1114, 8 in the throw
        Assert.That(logs.Events(1114), Has.Count.EqualTo(2));
        Assert.That(logs.Events(1116), Has.Count.EqualTo(2));
        Assert.That(logs.Events(1109), Is.Empty);
    }

    [Test]
    public async Task Rebooter_RecoversAfterSoft()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        int callbacks = 0;
        var connector = new PipeConnector(time)
        {
            Before = (_, _) => rebooter.Calls.Count > 0 ? Task.CompletedTask : Resilient.Refused(),
            Serve = d => d.NakEverythingAsync(),
        };
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter);
        options.ConnectionRestored = (_, _) => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; };
        var connecting = Connect(connector, options);
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await using var client = await connecting;
        Assert.That(client.IsConnected, Is.True);
        Assert.That((connector.Attempts, callbacks), Is.EqualTo((4, 0)));
        Assert.That(rebooter.Calls, Is.EqualTo(new[] { RebootKind.Soft }));
        Assert.That(logs.Events(1109), Has.Exactly(1).Items);
        Assert.That(logs.Events(1105), Is.Empty);
    }

    [Test]
    public async Task FirstConnect_RebootContext()
    {
        var (time, rebooter) = (new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(time: time).WithRebooter(rebooter, new EscalatingRecoveryPolicy { SoftRebootAfter = 1, MaxRebootsPerLoss = 1 }));
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1, Step);
        var context = rebooter.Contexts.Sole();
        Assert.That((context.Target, context.LastRemoteEndPoint, context.Requested), Is.EqualTo(("pipe", (IPEndPoint?)null, false)));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, TimeSpan.FromSeconds(1));   // ~63 s of boot wait and backoff
        await Assert.ThatAsync(() => connecting, Throws.TypeOf<SocketException>());
    }

    [Test]
    public async Task FirstConnect_CancelDuringBootWait_Throws_NothingRuns()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        using var cancel = new CancellationTokenSource();
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter, new EscalatingRecoveryPolicy { SoftRebootAfter = 1 }), cancel.Token);
        await time.AdvanceUntilAsync(() => logs.Events(1116).Count == 1, Step);         // inside the 2 s boot wait
        await cancel.CancelAsync();
        await Assert.ThatAsync(() => connecting.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.That(
            refused.Attempts,
            Is.EqualTo(1));                                               // nothing went on after the cancellation
        Assert.That(logs.Events(1109), Is.Empty);
    }

    [Test]
    public async Task FirstConnect_PolicyGivesUp_ThrowsLastFailure()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter, new RecordingPolicy { Decide = _ => RecoveryAction.GiveUp }));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThatAsync(() => connecting, Throws.TypeOf<SocketException>());
        Assert.That(refused.Attempts, Is.EqualTo(1));
        Assert.That(logs.Events(1106), Is.Empty);
        Assert.That(rebooter.Calls, Is.Empty);
    }

    [Test]
    public async Task FirstLoss_CountersStartAtZero()
    {
        var (logs, time, rebooter, policy) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter(), new RecordingPolicy());
        var connector = new PipeConnector(time)
        {
            Before = (_, _) => rebooter.Calls.Count == 0 ? Resilient.Refused() : Task.CompletedTask,
            Serve = d => d.NakEverythingAsync(),
        };
        policy.Decide = new EscalatingRecoveryPolicy().OnAttemptFailed;
        var connecting = Connect(connector, Resilient.Seam(logs, time).WithRebooter(rebooter, policy));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await using var client = await connecting;
        Assert.That(
            rebooter.Calls,
            Is.EqualTo(new[] { RebootKind.Soft }));                                  // one soft during the first connect
        policy.Decide = _ => RecoveryAction.Continue;
        policy.Calls.Clear();
        PipeDevice device = await connector.NextAsync();                                 // the only device: refused attempts attach none
        rebooter.Calls.Clear();                                                          // Before refuses again
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        await time.AdvanceUntilAsync(() => policy.Calls.Count >= 1, Step);
        Assert.That(
            (policy.Calls.First().FailedAttempts, policy.Calls.First().FailedAttemptsSinceReboot, policy.Calls.First().SoftReboots, policy.Calls.First().HardReboots),
            Is.EqualTo((1, 1, 0, 0)));
    }
}
