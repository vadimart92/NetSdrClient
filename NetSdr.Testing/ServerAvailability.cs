namespace NetSdr.Testing;

/// <summary>How <see cref="NetSdrTestServer"/> answers, to emulate a device that reboots or hangs.</summary>
public enum ServerAvailability
{
    /// <summary>Every connection is accepted and every request answered.</summary>
    Normal,

    /// <summary>
    /// Every new accepted connection is closed at once, without reading; the current connection is kept. A booting
    /// device looks exactly like this to the client: TCP is accepted, but the verification fails in phase Verify.
    /// </summary>
    CloseOnAccept,

    /// <summary>
    /// Requests are read and recorded in <see cref="NetSdrTestServer.Received"/>, but no reply is sent to them; this
    /// acts on the current connection too, from the moment of the change. Handlers do not run and the stored state
    /// does not change. An explicit <c>SendUnsolicitedAsync</c> and the UDP stream are not affected.
    /// </summary>
    Silent,
}
