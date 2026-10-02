namespace NetSdr.Data;

/// <summary>
/// Arithmetic on data-channel sequence numbers: they start at 0 when a capture starts, increase by one, and after
/// 0xFFFF continue with 1, so 0 appears only at the start of a capture.
/// </summary>
public static class DataSequence
{
    /// <summary>The number that follows <paramref name="sequence"/>: <c>sequence + 1</c>, with 0xFFFF followed by 1.</summary>
    public static ushort Next(ushort sequence) => sequence == ushort.MaxValue ? (ushort)1 : (ushort)(sequence + 1);

    /// <summary>
    /// How many packets lie between <paramref name="expected"/> and <paramref name="actual"/>, skipping the
    /// number 0 when the count wraps around. Zero means <paramref name="actual"/> is the expected packet.
    /// </summary>
    /// <remarks>
    /// The cycle is 65535 packets long, so a packet <c>k</c> behind <paramref name="expected"/> is at distance
    /// <c>65535 - k</c>: the result alone cannot tell a packet far ahead from one a little behind. A receiver treats a
    /// small number of packets behind as late and anything else as ahead, so a jump of more than about a full cycle
    /// (65535 minus the reorder window of 1024 packets) is taken for a late packet. See <see cref="NetSdrDataReceiver"/>.
    /// </remarks>
    public static int Distance(ushort expected, ushort actual) =>
        actual >= expected ? actual - expected : actual + ushort.MaxValue - expected;
}
