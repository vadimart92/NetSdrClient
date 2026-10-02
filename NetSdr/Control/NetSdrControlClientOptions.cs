namespace NetSdr.Control;

/// <summary>Settings of a <see cref="NetSdrControlClient"/>.</summary>
public sealed class NetSdrControlClientOptions
{
    /// <summary>
    /// How long a request waits for its reply once it has been sent. Must be positive, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait without limit.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Capacity of the <see cref="NetSdrControlClient.Unsolicited"/> channel. When it is full the oldest
    /// message is dropped. Must be at least 1.
    /// </summary>
    public int UnsolicitedCapacity { get; set; } = 256;

    /// <summary>
    /// Whether a response timeout puts the client into the faulted state. The protocol has no transaction
    /// identifiers, so a late reply could otherwise be taken for the answer to the next request with the same code.
    /// When it is <see langword="false"/> the connection stays up and the client remembers the one request that
    /// went unanswered. The same is done, whatever this setting says, for a request cancelled after it was sent and
    /// for a request failed by a reply for another item or of the wrong type. A later reply with the remembered item
    /// and type that does not answer the request in flight goes to <see cref="NetSdrControlClient.Unsolicited"/>.
    /// A reply that does answer the request in flight always completes it, so the client cannot get stuck
    /// when the device never answers the remembered request. The price: while the old reply is still on its way, a
    /// later request for the same item may receive that stale reply instead of its own, once; its own reply then goes
    /// to <see cref="NetSdrControlClient.Unsolicited"/> or answers the next request for that item.
    /// </summary>
    public bool FaultOnTimeout { get; set; } = true;
}
