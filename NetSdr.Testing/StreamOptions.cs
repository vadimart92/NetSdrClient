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
    /// The bytes of samples in a packet, without the 4-byte prefix. A payload that is not a whole number of samples is
    /// finished with zeros. When not set: 1024 or 512 bytes for 16-bit samples and 1440 or 384 for 24-bit ones, the large
    /// or small size of the specification (the small size only when the state asks for it).
    /// </summary>
    public int? PayloadSize { get; set; }

    /// <summary>The sample rate in hertz, which sets the pace of <see cref="Pacing.RealTime"/>; 200 000 when not set.</summary>
    public double? SampleRate { get; set; }

    /// <summary>
    /// The number of channels the samples are interleaved from. It only sets the pace of <see cref="Pacing.RealTime"/>:
    /// a packet takes (samples in the packet / <see cref="Channels"/>) / <see cref="SampleRate"/> seconds.
    /// </summary>
    public int Channels { get; set; } = 1;

    /// <summary>How fast packets are sent.</summary>
    public Pacing Pacing { get; set; } = Pacing.RealTime;

    /// <summary>Where the samples come from; the counter of <see cref="SampleSources.Counter"/> by default.</summary>
    public FillSamples Source { get; set; } = SampleSources.Counter();

    /// <summary>
    /// Decides for a sequence number whether that packet is left unsent. The number and the samples it would have
    /// carried are used up, so the receiver sees a gap.
    /// </summary>
    public Func<ushort, bool>? DropPacket { get; set; }
}
