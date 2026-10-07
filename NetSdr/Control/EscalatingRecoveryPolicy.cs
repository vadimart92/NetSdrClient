namespace NetSdr.Control;

/// <summary>
/// The default <see cref="IRecoveryPolicy"/>: a soft reboot after <see cref="SoftRebootAfter"/> failed attempts, a hard
/// reboot after <see cref="HardRebootAfter"/> more, at most <see cref="MaxRebootsPerLoss"/> reboots per loss, then
/// ordinary attempts only. It never returns <see cref="RecoveryAction.GiveUp"/>: the attempt limit stays with
/// <see cref="ResilientControlClientOptions.ReconnectAttempts"/>.
/// </summary>
public sealed class EscalatingRecoveryPolicy : IRecoveryPolicy
{
    private readonly int _softRebootAfter = 3;
    private readonly int _hardRebootAfter = 3;
    private readonly int _maxRebootsPerLoss = 2;

    /// <summary>Failed attempts of a loss without any reboot before a soft reboot. At least 1; 3 by default.</summary>
    public int SoftRebootAfter
    {
        get => _softRebootAfter;
        init => _softRebootAfter = value >= 1 ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "SoftRebootAfter must be at least 1.");
    }

    /// <summary>Failed attempts since the last reboot of a loss before a hard reboot. At least 1; 3 by default.</summary>
    public int HardRebootAfter
    {
        get => _hardRebootAfter;
        init => _hardRebootAfter = value >= 1 ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "HardRebootAfter must be at least 1.");
    }

    /// <summary>Reboots of one loss, soft and hard together, after which the policy only continues. Not negative; 2 by default.</summary>
    public int MaxRebootsPerLoss
    {
        get => _maxRebootsPerLoss;
        init => _maxRebootsPerLoss = value >= 0 ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRebootsPerLoss must not be negative.");
    }

    /// <inheritdoc/>
    public RecoveryAction OnAttemptFailed(RecoveryContext context)
    {
        var reboots = context.SoftReboots + context.HardReboots;
        var sinceReboot = context.FailedAttemptsSinceReboot;
        if (reboots >= MaxRebootsPerLoss)
            return RecoveryAction.Continue;
        if (reboots == 0 && sinceReboot >= SoftRebootAfter)
            return RecoveryAction.Reboot(RebootKind.Soft);
        if (reboots >= 1 && sinceReboot >= HardRebootAfter)
            return RecoveryAction.Reboot(RebootKind.Hard);
        return RecoveryAction.Continue;
    }
}
