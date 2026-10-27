using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetSdr.Data;

/// <summary>Settings of a <see cref="NetSdrDataReceiver"/>, read when the receiver is constructed.</summary>
public sealed class DataReceiverOptions
{
    /// <summary>
    /// Whether a datagram is dropped when the length in its header differs from the datagram length. A header that
    /// encodes 0 matches any datagram of 8192 bytes or more, so jumbo datagrams pass with the check on; the samples are
    /// then everything after the first 4 bytes. Turn it off only for a device whose headers are wrong.
    /// </summary>
    public bool ValidateLength { get; set; } = true;

    /// <summary>
    /// When set, only datagrams from this IPv4 address are accepted and all others are dropped. All addresses
    /// are accepted when it is <see langword="null"/>.
    /// </summary>
    public IPAddress? RemoteAddress { get; set; }

    /// <summary>
    /// Size of the socket receive buffer (<c>SO_RCVBUF</c>) in bytes, set when the receiver is constructed.
    /// <see cref="NetSdrDataReceiver.SetReceiveBuffer(int)"/> changes it later.
    /// </summary>
    public int InitialReceiveBufferBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Priority of the receive thread.</summary>
    public ThreadPriority ThreadPriority { get; set; } = ThreadPriority.AboveNormal;

    /// <summary>
    /// Creates the receiver's logger, category <c>NetSdr.Data.NetSdrDataReceiver</c>. Defaults to
    /// <see cref="NullLoggerFactory.Instance"/>, which logs nothing; <see langword="null"/> is rejected by the receiver's
    /// constructor. Nothing is logged per packet: start and stop, sequence gaps, the first handler error of an interval
    /// and the summaries of <see cref="StatisticsLogInterval"/>.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>
    /// How often the receive thread logs a summary of the counters: at Debug when nothing was lost, rejected or failed
    /// in the handler during the interval, at Warning otherwise. There is no timer: the thread looks at the clock once
    /// every 256 datagrams, so with no datagrams there are no summaries. Must be positive (at most
    /// <see cref="int.MaxValue"/> milliseconds), or <see cref="Timeout.InfiniteTimeSpan"/> to turn the summaries off.
    /// </summary>
    public TimeSpan StatisticsLogInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The clock of the summaries and of the durations in the logs. Set only by tests.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
