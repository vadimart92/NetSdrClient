using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    /// later request for the same item may receive that stale reply instead of its own; its own reply then goes
    /// to <see cref="NetSdrControlClient.Unsolicited"/> or answers the next request for that item. A device that is
    /// slow to answer can therefore keep a tight loop of requests for the same item one reply behind.
    /// </summary>
    /// <remarks>
    /// A NAK carries no item code, so the client cannot tell which request it rejects: it always goes to the request
    /// in flight. While a request is remembered as unanswered, a late NAK for it therefore fails the next request
    /// (and the remembered request stays remembered); that is a limit of the protocol, not of this setting.
    /// </remarks>
    public bool FaultOnTimeout { get; set; } = true;

    /// <summary>
    /// Creates the client's logger, category <c>NetSdr.Control.NetSdrControlClient</c>. Defaults to
    /// <see cref="NullLoggerFactory.Instance"/>, which logs nothing; <see langword="null"/> is rejected by the client's constructor.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>The clock of <see cref="ResponseTimeout"/> and of the durations in the logs. Set only by tests.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Whether the client belongs to a <c>ResilientControlClient</c>, which reports the fate of the connection itself:
    /// connect, close, failure and timeout events are then logged at Debug instead of Information, Error and Warning.
    /// Set only by <c>ResilientControlClient</c>.
    /// </summary>
    internal bool Supervised { get; set; }
}
