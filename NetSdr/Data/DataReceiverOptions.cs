using System.Net;

namespace NetSdr.Data;

/// <summary>Settings of a <see cref="NetSdrDataReceiver"/>, read when the receiver is constructed.</summary>
public sealed class DataReceiverOptions
{
    /// <summary>
    /// Whether a datagram is dropped when the length in its header differs from the datagram length. Turn it off for
    /// a device that sends datagrams over 8194 bytes: the header cannot describe them, and the samples are then
    /// everything after the first 4 bytes.
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
}
