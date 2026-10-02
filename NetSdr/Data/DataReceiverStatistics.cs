namespace NetSdr.Data;

/// <summary>
/// Counters of a <see cref="NetSdrDataReceiver"/>. The values are read one after another while the receive thread
/// keeps counting, so they are not a consistent cut across fields.
/// </summary>
/// <param name="Received">Packets delivered to the handler, including those whose handler threw.</param>
/// <param name="Bytes">Sample bytes of the delivered packets (the datagram length minus the 4-byte prefix).</param>
/// <param name="Lost">
/// Packets the sequence numbers show as missing, the sum of <see cref="DataPacketInfo.GapBefore"/>. A jump of more
/// than about a full sequence cycle (65535 minus 1024 packets) cannot be told from a late packet and is not counted;
/// a capture restarted while its packet 0 is lost shows up as a forward gap against the previous capture (use a new
/// receiver for each capture if that matters).
/// </param>
/// <param name="Rejected">Datagrams dropped: from another address, too short, not of the data type, or of the wrong length.</param>
/// <param name="HandlerErrors">Handler calls that ended with an exception.</param>
public readonly record struct DataReceiverStatistics(
    long Received, long Bytes, long Lost, long Rejected, long HandlerErrors);
