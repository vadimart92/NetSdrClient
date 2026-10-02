namespace NetSdr.Data;

/// <summary>
/// Called on the receive thread for every accepted datagram. <paramref name="samples"/> is valid only during the call.
/// Long work here makes the socket buffer overflow, so hand the data over quickly. An exception is counted in
/// <see cref="DataReceiverStatistics.HandlerErrors"/> and reception goes on.
/// </summary>
/// <param name="info">What the receiver knows about the packet.</param>
/// <param name="samples">The datagram without its 4-byte prefix (header and sequence number).</param>
public delegate void DataPacketHandler(in DataPacketInfo info, ReadOnlySpan<byte> samples);

/// <summary>Metadata of one data packet.</summary>
public readonly struct DataPacketInfo
{
    internal DataPacketInfo(ushort sequence, int gapBefore, SampleFormat format, long timestamp, DateTime utcTime)
    {
        Sequence = sequence;
        GapBefore = gapBefore;
        Format = format;
        Timestamp = timestamp;
        UtcTime = utcTime;
    }

    /// <summary>The sequence number from the datagram.</summary>
    public ushort Sequence { get; }

    /// <summary>
    /// How many packets went missing right before this one; 0 for the first packet, at the start of a capture, and
    /// for a packet that arrived late (out of order or twice, at most about 1000 packets behind the expected one).
    /// A jump of more than about a full sequence cycle (65535 minus 1024 packets) cannot be told from a late
    /// packet, so it is not reported as a gap. A capture restarted while its packet 0 is lost shows up as a forward
    /// gap against the previous capture; use a new receiver for each capture if that matters.
    /// </summary>
    public int GapBefore { get; }

    /// <summary>The packet starts a capture (<see cref="Sequence"/> is 0).</summary>
    public bool IsCaptureStart => Sequence == 0;

    /// <summary>Sample layout deduced from the datagram length.</summary>
    public SampleFormat Format { get; }

    /// <summary>Arrival time as <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> ticks; monotonic.</summary>
    public long Timestamp { get; }

    /// <summary>Arrival time on the wall clock, for stamping recordings.</summary>
    public DateTime UtcTime { get; }
}
