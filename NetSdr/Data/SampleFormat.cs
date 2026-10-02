namespace NetSdr.Data;

/// <summary>Layout of the samples in a data datagram, told apart by the datagram length.</summary>
public enum SampleFormat : byte
{
    /// <summary>The datagram length matches no known packet size.</summary>
    Unknown = 0,

    /// <summary>16-bit I and Q samples (4 bytes per complex sample).</summary>
    Int16 = 1,

    /// <summary>24-bit I and Q samples (6 bytes per complex sample).</summary>
    Int24 = 2,
}
