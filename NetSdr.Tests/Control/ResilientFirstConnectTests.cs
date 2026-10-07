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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidConnectAttempts_Throws(int attempts)
    {
        var options = new ResilientControlClientOptions { ConnectAttempts = attempts };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), options); });
        Assert.Contains("ConnectAttempts must be at least 1.", ex.Message);
    }

    [Fact]
    public async Task RecoveryPolicyWithoutRebooter_IsAllowed()
    {
        var options = new ResilientControlClientOptions { RecoveryPolicy = new EscalatingRecoveryPolicy() };   // validated, never called
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThrowsAsync<SocketException>(() => ResilientControlClient.ConnectAsync(refused.ConnectAsync, "pipe", options, default).WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task NoRebooter_DefaultIsOneAttempt()
    {
        var logs = new FakeLoggerFactory();
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThrowsAsync<SocketException>(() => Connect(refused, Resilient.Seam(logs)).WaitAsync(Limits.Test));
        Assert.Equal(1, refused.Attempts);
        Assert.Empty(logs.Events(1118));
    }

    [Fact]
    public async Task ExplicitConnectAttempts_RetriesWithBackoff()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var options = Resilient.Seam(logs, time);
        options.ConnectAttempts = 3;
        DateTimeOffset start = time.GetUtcNow();
        var connecting = Connect(refused, options);
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThrowsAsync<SocketException>(() => connecting);
        double[] starts = refused.AttemptTimes.Select(t => (t - start).TotalSeconds).ToArray();
        double[] expected = [0, 1, 3];
        Assert.Equal(3, starts.Length);
        for (int i = 0; i < 3; i++)
            Assert.InRange(starts[i], expected[i], expected[i] + 0.3);
        Assert.Equal(new (string?, string?, string?, double)[] { ("1", "3", "Connect", 1.0), ("2", "3", "Connect", 2.0) },
            logs.Events(1118).Select(r => (r.Value("Attempt"), r.Value("Attempts"), r.Value("Phase"), r.Span("Delay").TotalSeconds)));
        Assert.All(logs.Events(1118), r => Assert.Equal((LogLevel.Warning, "pipe"), (r.Level, r.Value("Target"))));
        Assert.Empty(logs.Events(1104));
    }

    [Fact]
    public async Task Rebooter_DefaultEightAttempts_FullLadder()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThrowsAsync<SocketException>(() => connecting);
        Assert.Equal(8, refused.Attempts);
        Assert.Equal(new[] { RebootKind.Soft, RebootKind.Hard }, rebooter.Calls);
        Assert.Equal(5, logs.Events(1118).Count);                                        // attempts 1, 2, 4, 5, 7; 3 and 6 end in 1114, 8 in the throw
        Assert.Equal(2, logs.Events(1114).Count);
        Assert.Equal(2, logs.Events(1116).Count);
        Assert.Empty(logs.Events(1109));
    }

    [Fact]
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
        Assert.True(client.IsConnected);
        Assert.Equal((4, 0), (connector.Attempts, callbacks));
        Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
        Assert.Single(logs.Events(1109));
        Assert.Empty(logs.Events(1105));
    }

    [Fact]
    public async Task FirstConnect_RebootContext()
    {
        var (time, rebooter) = (new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(time: time).WithRebooter(rebooter, new EscalatingRecoveryPolicy { SoftRebootAfter = 1, MaxRebootsPerLoss = 1 }));
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1, Step);
        var context = Assert.Single(rebooter.Contexts);
        Assert.Equal(("pipe", null, false), (context.Target, context.LastRemoteEndPoint, context.Requested));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, TimeSpan.FromSeconds(1));   // ~63 s of boot wait and backoff
        await Assert.ThrowsAsync<SocketException>(() => connecting);
    }

    [Fact]
    public async Task FirstConnect_CancelDuringBootWait_Throws_NothingRuns()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        using var cancel = new CancellationTokenSource();
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter, new EscalatingRecoveryPolicy { SoftRebootAfter = 1 }), cancel.Token);
        await time.AdvanceUntilAsync(() => logs.Events(1116).Count == 1, Step);         // inside the 2 s boot wait
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(Limits.Test));
        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.Equal(1, refused.Attempts);                                               // nothing went on after the cancellation
        Assert.Empty(logs.Events(1109));
    }

    [Fact]
    public async Task FirstConnect_PolicyGivesUp_ThrowsLastFailure()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter, new RecordingPolicy { Decide = _ => RecoveryAction.GiveUp }));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThrowsAsync<SocketException>(() => connecting);
        Assert.Equal(1, refused.Attempts);
        Assert.Empty(logs.Events(1106));
        Assert.Empty(rebooter.Calls);
    }

    [Fact]
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
        Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);                                  // one soft during the first connect
        policy.Decide = _ => RecoveryAction.Continue;
        policy.Calls.Clear();
        PipeDevice device = await connector.NextAsync();                                 // the only device: refused attempts attach none
        rebooter.Calls.Clear();                                                          // Before refuses again
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        await time.AdvanceUntilAsync(() => policy.Calls.Count >= 1, Step);
        Assert.Equal((1, 1, 0, 0), (policy.Calls.First().FailedAttempts, policy.Calls.First().FailedAttemptsSinceReboot, policy.Calls.First().SoftReboots, policy.Calls.First().HardReboots));
    }
}
