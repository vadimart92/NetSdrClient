using NetSdr.Data;

namespace NetSdr.Testing;

/// <summary>
/// Fills the samples of one data packet. The samples are complex: each is an I component followed by a Q component,
/// each component 2 bytes (<see cref="SampleFormat.Int16"/>) or 3 bytes (<see cref="SampleFormat.Int24"/>), little-endian.
/// </summary>
/// <param name="destination">The bytes to fill; whole samples only, the test server never passes a partial one.</param>
/// <param name="firstSampleIndex">The index, counted from the start of the stream, of the first sample in <paramref name="destination"/>.</param>
/// <param name="format">The sample layout.</param>
public delegate void FillSamples(Span<byte> destination, long firstSampleIndex, SampleFormat format);

/// <summary>How fast the test server sends data packets.</summary>
public enum Pacing
{
    /// <summary>At the pace of the sample rate, in bursts when the sender falls behind.</summary>
    RealTime,

    /// <summary>As fast as the sender can go, without pauses.</summary>
    Unthrottled,
}

/// <summary>
/// Settings of a data stream from <see cref="NetSdrTestServer"/>. The server reads them when a stream starts, so a
/// change reaches the next stream, not the running one. A <see langword="null"/> <see cref="Format"/>,
/// <see cref="PayloadSize"/> or <see cref="SampleRate"/> is taken from the device state when the stream was started by a
/// Run command, and from the defaults otherwise; a value that is set wins over the state.
/// </summary>
public sealed class StreamOptions
{
    /// <summary>The sample format; 16-bit when not set and not taken from the state.</summary>
    public SampleFormat? Format { get; set; }

    /// <summary>
    /// The bytes of a packet after the 4-byte prefix: the samples, and the timestamp in the last 8 bytes when
    /// <see cref="Timestamps"/> is on, so 8192 carries 8184 bytes of samples then. Room that is not a whole number of
    /// samples is filled with zeros. A datagram over 8191 bytes gets a header that encodes 0, which a
    /// <c>NetSdrDataReceiver</c> accepts. When not set: 1024 or 512 bytes for 16-bit samples and 1440 or 384 for
    /// 24-bit ones, the large or small size of the specification (the small size only when the state asks for it).
    /// Ignored when <see cref="Packets"/> is set.
    /// </summary>
    public int? PayloadSize { get; set; }

    /// <summary>
    /// The samples of the packets to send, one entry per packet, played in order and over again from the first. Each
    /// entry must hold whole samples of <see cref="Format"/>, at least one, and becomes a packet of exactly that many
    /// bytes, plus the timestamp when <see cref="Timestamps"/> is on. The sample index of a packet counts the samples of
    /// the packets before it. When set, <see cref="Source"/> and <see cref="PayloadSize"/> are ignored. The list is
    /// copied when a stream starts; the memory of the entries is read on every packet and not copied.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? Packets { get; set; }

    /// <summary>
    /// Whether every packet ends with a <c>PacketTimestamp</c> (8 bytes) of its first sample: sample index
    /// <c>k</c> is <c>k / <see cref="Channels"/></c> sample periods of <see cref="SampleRate"/> after
    /// <see cref="TimestampOrigin"/>.
    /// </summary>
    public bool Timestamps { get; set; }

    /// <summary>The time of the first sample of the stream, for <see cref="Timestamps"/>; the clock at the start of the stream when not set.</summary>
    public DateTimeOffset? TimestampOrigin { get; set; }

    /// <summary>The sample rate in hertz, which sets the pace of <see cref="Pacing.RealTime"/>; 200 000 when not set.</summary>
    public double? SampleRate { get; set; }

    /// <summary>
    /// The number of channels the samples are interleaved from. It only sets the pace of <see cref="Pacing.RealTime"/>:
    /// a packet takes (samples in the packet / <see cref="Channels"/>) / <see cref="SampleRate"/> seconds.
    /// </summary>
    public int Channels { get; set; } = 1;

    /// <summary>How fast packets are sent.</summary>
    public Pacing Pacing { get; set; } = Pacing.RealTime;

    /// <summary>
    /// Where the samples come from; the counter of <see cref="SampleSources.Counter"/> by default. Ignored when
    /// <see cref="Packets"/> is set.
    /// </summary>
    public FillSamples Source { get; set; } = SampleSources.Counter();

    /// <summary>
    /// Decides for a sequence number whether that packet is left unsent. The number and the samples it would have
    /// carried are used up, so the receiver sees a gap.
    /// </summary>
    public Func<ushort, bool>? DropPacket { get; set; }
}
