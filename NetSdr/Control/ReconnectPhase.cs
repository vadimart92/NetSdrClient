namespace NetSdr.Control;

/// <summary>The phase a reconnection attempt was in when it failed, as event 1104 reports it.</summary>
public enum ReconnectPhase
{
    /// <summary>Establishing the TCP connection.</summary>
    Connect,

    /// <summary>Waiting for the device to answer the verification request.</summary>
    Verify,

    /// <summary>Running <see cref="ResilientControlClientOptions.ConnectionRestored"/>.</summary>
    Restore,
}
