namespace NetSdr.Control;

/// <summary>What <see cref="ResilientControlClientOptions.ConnectionRestored"/> gets to restore the device's session on a new connection.</summary>
public sealed class ConnectionRestoredContext
{
    internal ConnectionRestoredContext(INetSdrControlClient client, Exception cause, DateTimeOffset lostAt, RebootKind? afterReboot)
    {
        Client = client;
        Cause = cause;
        LostAt = lostAt;
        AfterReboot = afterReboot;
    }

    /// <summary>
    /// The new connection, valid until the callback's task completes; afterwards every call throws
    /// <see cref="InvalidOperationException"/>, and during <see cref="ResilientControlClient.DisposeAsync"/>
    /// <see cref="ObjectDisposedException"/>. Its requests keep the line discipline and the late-reply adoption of the
    /// <see cref="ResilientControlClient"/>, but a lost connection fails them with <see cref="IOException"/> right away:
    /// they never move to another connection. <see cref="INetSdrControlClient.Unsolicited"/> and
    /// <see cref="INetSdrControlClient.Completion"/> are those of the <see cref="ResilientControlClient"/>, so a callback
    /// that reads <c>Unsolicited</c> takes messages from the application. <see cref="IAsyncDisposable.DisposeAsync"/>
    /// does nothing, so a device object wrapped around this client can be used and disposed inside the callback.
    /// The connection's ends are <see cref="INetSdrControlClient.LocalEndPoint"/> and
    /// <see cref="INetSdrControlClient.RemoteEndPoint"/> of this client; the local port is new on every reconnection.
    /// </summary>
    public INetSdrControlClient Client { get; }

    /// <summary>Why the previous connection was declared lost.</summary>
    public Exception Cause { get; }

    /// <summary>When the loss was noticed; it marks the gap in the I/Q stream for the application.</summary>
    public DateTimeOffset LostAt { get; }

    /// <summary>
    /// The kind of the last accepted reboot of this loss, or <see langword="null"/> when the device was not rebooted
    /// during it. A rebooted device has lost its whole state, not only the session of the connection.
    /// </summary>
    public RebootKind? AfterReboot { get; }
}
