namespace NetSdr.Control;

/// <summary>
/// Device reboots (reboot spec 4 and 5): the slot of a manual <c>RebootAsync</c> request and the types that carry a
/// reboot decision out of an attempt series.
/// </summary>
public sealed partial class ResilientControlClient
{
    /// <summary>
    /// Wakes a watch or an attempt series when a manual reboot request is registered; replaced whenever a request is
    /// taken. Guarded by <see cref="_sync"/>.
    /// </summary>
    private CancellationTokenSource _rebootWake = new();

    /// <summary>A manual reboot request: its kind (Hard wins when requests merge) and the callers waiting for it.</summary>
    private sealed class RebootRequest
    {
        public RebootKind Kind { get; set; }

        /// <summary>Completes once the client is connected again after the reboot, or fails with what failed it.</summary>
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the reboot step has started; a request that runs is joined, not merged.</summary>
        public bool Running { get; set; }
    }

    /// <summary>Ends an attempt series because the recovery policy decided on a reboot; never leaves the client.</summary>
    private sealed class RebootScheduledException(RebootKind kind, Exception failure) : Exception("A reboot was scheduled.", failure)
    {
        public RebootKind Kind { get; } = kind;
    }

    /// <summary>Ends an attempt series because the recovery policy gave up; never leaves the client.</summary>
    private sealed class RecoveryGaveUpException(Exception failure) : Exception("The recovery policy gave up.", failure);
}
