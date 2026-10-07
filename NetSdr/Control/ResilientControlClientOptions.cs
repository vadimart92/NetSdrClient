using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetSdr.Control;

/// <summary>
/// Settings of a <see cref="ResilientControlClient"/>. <c>ConnectAsync</c> validates and copies them, so later
/// changes to this instance do not reach the client.
/// </summary>
public sealed class ResilientControlClientOptions
{
    /// <summary>
    /// How long a request waits for its reply once it has been sent. Must be positive and at most
    /// <see cref="int.MaxValue"/> milliseconds; there is no infinite setting, because this timeout also tells a busy
    /// device from a dead connection (see <see cref="LateReplyTimeout"/>).
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How much longer a request that got no reply within <see cref="ResponseTimeout"/> waits for a late one before
    /// the connection is declared dead and replaced. Must be positive, and <see cref="ResponseTimeout"/> plus this
    /// must be at most <see cref="int.MaxValue"/> milliseconds. While the late reply is awaited nothing else is
    /// written to the connection, and the retry of the command adopts the late reply instead of sending the request again.
    /// </summary>
    public TimeSpan LateReplyTimeout { get; set; } = TimeSpan.FromSeconds(13);

    /// <summary>
    /// The limit of one whole command call: the queue behind other commands, every attempt, the wait for a late reply
    /// and the wait for a reconnection. Must be positive (at most <see cref="int.MaxValue"/> milliseconds) or
    /// <see cref="Timeout.InfiniteTimeSpan"/>. A command that runs out of time fails with <see cref="TimeoutException"/>.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The silence on the connection after which a <c>Get</c> of the status codes is sent to prove the device is still
    /// there. Must be positive (at most <see cref="int.MaxValue"/> milliseconds), or <see cref="Timeout.InfiniteTimeSpan"/>
    /// to send no heartbeats. A dead idle connection is then noticed after
    /// <see cref="HeartbeatInterval"/> + <see cref="ResponseTimeout"/> + <see cref="LateReplyTimeout"/>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The limit of one TCP connection attempt. Must be positive (at most <see cref="int.MaxValue"/> milliseconds) or
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many connection attempts one lost connection gets before the client gives up for good. Must be at least 1;
    /// <see cref="int.MaxValue"/> means never giving up. The attempts start about 1, 2, 4, 8, 16, 30, 30... seconds apart.
    /// </summary>
    public int ReconnectAttempts { get; set; } = int.MaxValue;

    /// <summary>
    /// Capacity of the <see cref="ResilientControlClient.Unsolicited"/> channel. When it is full the oldest message
    /// is dropped. Must be at least 1.
    /// </summary>
    public int UnsolicitedCapacity { get; set; } = 256;

    /// <summary>
    /// Restores the device's session state on a new connection. The device forgets everything the application set
    /// when the connection drops, and the framework never replays it: this callback is the one place to do that.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>It runs after every reconnection, not after <c>ConnectAsync</c>, on the new connection once the device has
    /// answered the verification request, and before any command of the application reaches that connection.</item>
    /// <item>Its requests go through <see cref="ConnectionRestoredContext.Client"/>, never through the
    /// <see cref="ResilientControlClient"/>: calling the <see cref="ResilientControlClient"/> from the callback throws
    /// <see cref="InvalidOperationException"/> and makes the client give up reconnecting.</item>
    /// <item>Any other exception, a <see cref="NetSdrNakException"/> included, fails only this attempt: the connection
    /// is closed and a new one is tried after the backoff.</item>
    /// <item>The token is cancelled by <see cref="ResilientControlClient.DisposeAsync"/>.</item>
    /// <item>A lock of the application that is held by a thread waiting for a command, and that the callback needs,
    /// blocks both until <see cref="CommandTimeout"/>: the command waits for the callback to return, and the callback
    /// waits for the lock.</item>
    /// </list>
    /// </remarks>
    public Func<ConnectionRestoredContext, CancellationToken, Task>? ConnectionRestored { get; set; }

    /// <summary>
    /// Creates the loggers of the client (category <c>NetSdr.Control.ResilientControlClient</c>) and of its inner
    /// connections (category <c>NetSdr.Control.NetSdrControlClient</c>). Defaults to <see cref="NullLoggerFactory.Instance"/>,
    /// which logs nothing; <see langword="null"/> is rejected by <c>ConnectAsync</c>.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>The one clock of every timeout, delay and timestamp of the client. Set only by tests.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Whether the reconnection delays are jittered. Set only by tests.</summary>
    internal bool UseJitter { get; set; } = true;
}
