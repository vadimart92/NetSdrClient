namespace NetSdr.Control;

/// <summary>Settings of a <see cref="NetSdrControlClient"/>.</summary>
public sealed class NetSdrControlClientOptions
{
    /// <summary>How long a request waits for its reply.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Capacity of the <see cref="NetSdrControlClient.Unsolicited"/> channel. When it is full the oldest
    /// message is dropped. Must be at least 1.
    /// </summary>
    public int UnsolicitedCapacity { get; set; } = 256;

    /// <summary>
    /// Whether a response timeout puts the client into the faulted state. The protocol has no transaction
    /// identifiers, so a late reply could otherwise be taken for the answer to the next request with the same code.
    /// </summary>
    public bool FaultOnTimeout { get; set; } = true;
}
