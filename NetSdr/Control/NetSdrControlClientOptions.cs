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
    /// went unanswered (the same applies to a request cancelled after it was sent): the first reply with its item
    /// and type goes to <see cref="NetSdrControlClient.Unsolicited"/> instead of answering a later request. If the
    /// device never answers that request, the reply to the next request for the same item is taken for it and that
    /// request times out in its place.
    /// </summary>
    public bool FaultOnTimeout { get; set; } = true;
}
