using NetSdr.Framing;
using Polly;

namespace NetSdr.Control;

/// <summary>
/// The private nested types of the client: the client state, one connection (<see cref="Link"/>), one written
/// request (<see cref="Exchange"/>), one command across its attempts (<see cref="CommandExecution"/>) and one loss
/// across its reconnection attempts (<see cref="ReconnectState"/>).
/// </summary>
public sealed partial class ResilientControlClient
{
    private enum ClientState
    {
        Connected,
        Reconnecting,
        Closed,
    }

    /// <summary>One TCP connection: its inner client and the line discipline on it (spec 6.3).</summary>
    private sealed class Link(NetSdrControlClient client)
    {
        public NetSdrControlClient Client { get; } = client;

        /// <summary>Held from the write of a request until the exchange is resolved: by a reply, a NAK, a late reply or the loss of the connection.</summary>
        public SemaphoreSlim Wire { get; } = new(1, 1);

        /// <summary>The unresolved exchange on the line; written under the client's lock.</summary>
        public Exchange? Current { get; set; }

        /// <summary>The timestamp of the last frame received on this connection.</summary>
        public long LastHeard;

        /// <summary>Why the resilient client closed this connection itself, when it did.</summary>
        public Exception? LossCause { get; set; }

        /// <summary>Spec 6.8. Completed until the TCP connection exists, so a failed connect has nothing to await.</summary>
        public Task Pump { get; set; } = Task.CompletedTask;

        public void Heard(TimeProvider time) => Volatile.Write(ref LastHeard, time.GetTimestamp());
    }

    /// <summary>One written request and its fate (spec 6.3).</summary>
    private sealed class Exchange(Link link, RequestType type, ushort code, string? item, long sentAt)
    {
        public Link Link { get; } = link;
        public RequestType Type { get; } = type;
        public ushort Code { get; } = code;
        public string? Item { get; } = item;
        public long SentAt { get; } = sentAt;

        /// <summary>The inner client's request task; set right after the exchange is current.</summary>
        public Task<ControlItemMessage> Request { get; set; } = null!;

        /// <summary>How the exchange ended, for whoever waits for it after its response timeout.</summary>
        public TaskCompletionSource<Resolution> Late { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Written under the client's lock.</summary>
        public ExchangeState State { get; set; }

        /// <summary>The timestamp of the resolution; written under the client's lock, valid once <see cref="State"/> is Resolved.</summary>
        public long ResolvedAt { get; set; }

        /// <summary>The late-reply deadline of an unanswered exchange; written under the client's lock.</summary>
        public ITimer? Deadline { get; set; }
    }

    private enum ExchangeState
    {
        InFlight,
        Unanswered,
        Resolved,
    }

    private enum Outcome
    {
        /// <summary>A reply or a NAK answered the request while its caller waited.</summary>
        Answered,

        /// <summary>A late reply.</summary>
        Reply,

        /// <summary>A late NAK.</summary>
        Nak,

        /// <summary>The connection was lost before the request was answered.</summary>
        Lost,
    }

    private readonly record struct Resolution(Outcome Outcome, ControlItemMessage Message = default, Exception? Cause = null);

    /// <summary>One command of the application across its attempts (spec 8).</summary>
    private sealed class CommandExecution(
        ResilientControlClient owner, RequestType type, ushort code, ReadOnlyMemory<byte> payload, string? item)
    {
        public static readonly ResiliencePropertyKey<CommandExecution> ExecKey = new("NetSdr.Command");

        public ResilientControlClient Owner { get; } = owner;
        public RequestType Type { get; } = type;
        public ushort Code { get; } = code;
        public ReadOnlyMemory<byte> Payload { get; } = payload;
        public string? Item { get; } = item;

        /// <summary>The session of a <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback whose request this is; <see langword="null"/> for a command of the application.</summary>
        public RestoreSession? Session { get; init; }

        /// <summary>The connection a session request is bound to; <see langword="null"/> for a command of the application.</summary>
        public Link? Bound => Session?.Link;

        /// <summary>The exchange that timed out on a live connection; the next attempt adopts its late reply.</summary>
        public Exchange? Outstanding { get; set; }

        /// <summary>The failure of the last attempt that was retried.</summary>
        public Exception? LastError { get; set; }
    }

    /// <summary>The state of one loss across the attempts of the reconnection pipeline (spec 8).</summary>
    private sealed class ReconnectState(Exception cause, DateTimeOffset lostAt, long lostTimestamp)
    {
        /// <summary>Why the connection was lost.</summary>
        public Exception Cause { get; } = cause;

        /// <summary>When it was lost, as <see cref="ConnectionRestoredContext"/> reports it.</summary>
        public DateTimeOffset LostAt { get; } = lostAt;

        /// <summary>The timestamp of the loss, for the downtime of event 1105.</summary>
        public long LostTimestamp { get; } = lostTimestamp;

        /// <summary>How many attempts this loss has had, the running one included.</summary>
        public int Attempt { get; set; }

        /// <summary>The phase the running attempt is in, for event 1104.</summary>
        public ReconnectPhase Phase { get; set; }
    }
}
