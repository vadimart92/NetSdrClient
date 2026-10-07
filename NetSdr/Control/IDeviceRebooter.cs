namespace NetSdr.Control;

/// <summary>Reboots the device through a channel of the developer's own, outside the NetSDR protocol.</summary>
public interface IDeviceRebooter
{
    /// <summary>
    /// Asks the device to reboot through the developer's own channel. Completes when the device has accepted
    /// the request, not when it has booted. Throws when the request could not be made or was refused.
    /// </summary>
    /// <param name="kind">The kind of reboot.</param>
    /// <param name="context">What the client knows about the device it connects to.</param>
    /// <param name="ct">Cancelled when the client is disposed or the request takes longer than <see cref="ResilientControlClientOptions.RebootTimeout"/>.</param>
    Task RebootAsync(RebootKind kind, RebootContext context, CancellationToken ct);

    /// <summary>
    /// How long the device needs after an accepted request before it accepts connections again. Not negative.
    /// </summary>
    /// <param name="kind">The kind of reboot.</param>
    TimeSpan GetBootTime(RebootKind kind);
}
