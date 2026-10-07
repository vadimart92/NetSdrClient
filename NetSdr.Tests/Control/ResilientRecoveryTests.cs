using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;

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
}
