namespace NetSdr.Control;

/// <summary>Decides what <see cref="ResilientControlClient"/> does after a failed reconnection attempt.</summary>
public interface IRecoveryPolicy
{
    /// <summary>
    /// Called on the supervisor after every failed reconnection attempt while a Rebooter is configured.
    /// Must be fast and must not block.
    /// </summary>
    /// <param name="context">The failed attempt and the loss it belongs to.</param>
    RecoveryAction OnAttemptFailed(RecoveryContext context);
}

/// <summary>What <see cref="IRecoveryPolicy.OnAttemptFailed"/> knows about a failed attempt and its loss.</summary>
/// <param name="FailedAttempts">Failed attempts since the connection was lost.</param>
/// <param name="FailedAttemptsSinceReboot">Failed attempts since the last reboot of this loss (or since the loss).</param>
/// <param name="Phase">Where the attempt failed.</param>
/// <param name="Failure">What it failed with.</param>
/// <param name="Downtime">Since the loss was detected.</param>
/// <param name="SoftReboots">Soft reboots of this loss, attempted (a failed reboot counts).</param>
/// <param name="HardReboots">Hard reboots of this loss, attempted (a failed reboot counts).</param>
public readonly record struct RecoveryContext(
    int FailedAttempts,
    int FailedAttemptsSinceReboot,
    ReconnectPhase Phase,
    Exception Failure,
    TimeSpan Downtime,
    int SoftReboots,
    int HardReboots);

/// <summary>What an <see cref="IRecoveryPolicy"/> decides after a failed attempt.</summary>
public enum RecoveryActionKind
{
    /// <summary>Go on with the next attempt.</summary>
    Continue,

    /// <summary>Reboot the device, then go on with the next attempt.</summary>
    Reboot,

    /// <summary>Stop reconnecting; the client gives up as when its attempts are exhausted.</summary>
    GiveUp,
}

/// <summary>The decision of an <see cref="IRecoveryPolicy"/> after a failed attempt.</summary>
public readonly struct RecoveryAction : IEquatable<RecoveryAction>
{
    private RecoveryAction(RecoveryActionKind kind, RebootKind rebootKind)
    {
        Kind = kind;
        RebootKind = rebootKind;
    }

    /// <summary>Go on with the next attempt.</summary>
    public static RecoveryAction Continue { get; } = new(RecoveryActionKind.Continue, default);

    /// <summary>Stop reconnecting.</summary>
    public static RecoveryAction GiveUp { get; } = new(RecoveryActionKind.GiveUp, default);

    /// <summary>Reboot the device with <paramref name="kind"/>, then go on with the next attempt.</summary>
    /// <param name="kind">The kind of reboot.</param>
    public static RecoveryAction Reboot(RebootKind kind) => new(RecoveryActionKind.Reboot, kind);

    /// <summary>Continue, Reboot or GiveUp.</summary>
    public RecoveryActionKind Kind { get; }

    /// <summary>The kind of reboot; meaningful when <see cref="Kind"/> is <see cref="RecoveryActionKind.Reboot"/>.</summary>
    public RebootKind RebootKind { get; }

    /// <inheritdoc/>
    public bool Equals(RecoveryAction other) => Kind == other.Kind && RebootKind == other.RebootKind;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RecoveryAction other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, RebootKind);

    /// <summary>Whether two actions are equal.</summary>
    public static bool operator ==(RecoveryAction left, RecoveryAction right) => left.Equals(right);

    /// <summary>Whether two actions differ.</summary>
    public static bool operator !=(RecoveryAction left, RecoveryAction right) => !left.Equals(right);

    /// <summary>"Continue", "GiveUp" or "Reboot(Soft)" / "Reboot(Hard)".</summary>
    public override string ToString() => Kind == RecoveryActionKind.Reboot ? $"Reboot({RebootKind})" : Kind.ToString();
}
