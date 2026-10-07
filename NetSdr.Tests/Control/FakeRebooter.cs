using System.Collections.Concurrent;
using NetSdr.Control;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

/// <summary>A scripted <see cref="IDeviceRebooter"/>: records every call and does what <see cref="OnReboot"/> says.</summary>
internal sealed class FakeRebooter : IDeviceRebooter
{
    public ConcurrentQueue<RebootKind> Calls { get; } = new();

    public ConcurrentQueue<RebootContext> Contexts { get; } = new();

    /// <summary>The boot time of either kind unless <see cref="BootTimeOf"/> is set.</summary>
    public TimeSpan BootTime { get; set; } = TimeSpan.FromSeconds(2);

    public Func<RebootKind, TimeSpan>? BootTimeOf { get; set; }

    /// <summary>What a call does after it is recorded; <see langword="null"/> accepts at once.</summary>
    public Func<RebootKind, RebootContext, CancellationToken, Task>? OnReboot { get; set; }

    public Task RebootAsync(RebootKind kind, RebootContext context, CancellationToken ct)
    {
        Calls.Enqueue(kind);
        Contexts.Enqueue(context);
        return OnReboot?.Invoke(kind, context, ct) ?? Task.CompletedTask;
    }

    public TimeSpan GetBootTime(RebootKind kind) => BootTimeOf?.Invoke(kind) ?? BootTime;

    /// <summary>
    /// A rebooter of a test server: an accepted reboot makes it close new connections for <paramref name="bootTime"/> of
    /// real time, then serve normally again; the reported boot time is 100 ms longer.
    /// </summary>
    public static FakeRebooter Booting(NetSdrTestServer server, TimeSpan bootTime) => new()
    {
        BootTime = bootTime + TimeSpan.FromMilliseconds(100),
        OnReboot = (_, _, _) =>
        {
            server.Availability = ServerAvailability.CloseOnAccept;
            _ = Task.Delay(bootTime).ContinueWith(
                _ => server.Availability = ServerAvailability.Normal, TaskScheduler.Default);
            return Task.CompletedTask;
        },
    };
}

/// <summary>An <see cref="IRecoveryPolicy"/> that records every context and answers with <see cref="Decide"/>.</summary>
internal sealed class RecordingPolicy : IRecoveryPolicy
{
    public ConcurrentQueue<RecoveryContext> Calls { get; } = new();

    public Func<RecoveryContext, RecoveryAction> Decide { get; set; } = _ => RecoveryAction.Continue;

    public RecoveryAction OnAttemptFailed(RecoveryContext context)
    {
        Calls.Enqueue(context);
        return Decide(context);
    }
}
