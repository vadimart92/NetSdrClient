namespace NetSdr.Items;

/// <summary>
/// FPGA configuration, item 0x000C. A Set carries only <see cref="Selected"/>; the device answers a Get
/// with the selected configuration, the ID and revision of the loaded image, and a text description.
/// </summary>
public readonly struct FpgaConfiguration : IControlItem<FpgaConfiguration>
{
    private const int FixedSize = 3;

    private readonly string? _description;

    public static ushort Code => 0x000C;

    /// <summary>Builds a Set request that selects a configuration.</summary>
    public FpgaConfiguration(byte selected)
    {
        Selected = selected;
    }

    public FpgaConfiguration(byte selected, byte id, byte revision, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        Selected = selected;
        Id = id;
        Revision = revision;
        _description = description;
    }

    /// <summary>The selected configuration number.</summary>
    public byte Selected { get; }

    /// <summary>The ID of the loaded image.</summary>
    public byte Id { get; }

    /// <summary>The revision of the loaded image.</summary>
    public byte Revision { get; }

    /// <summary>The text description of the configuration; empty when the device did not send one.</summary>
    public string Description => _description ?? string.Empty;

    /// <summary>A Set carries only <see cref="Selected"/>, so one byte.</summary>
    public static int GetSize(in FpgaConfiguration item) => 1;

    /// <summary>Writes only <see cref="Selected"/>.</summary>
    public static void Write(in FpgaConfiguration item, Span<byte> destination) => destination[0] = item.Selected;

    /// <summary>Reads the three bytes <see cref="Selected"/>, <see cref="Id"/> and <see cref="Revision"/>, then ASCII text up to the first zero.</summary>
    /// <exception cref="ArgumentException">The payload is shorter than three bytes.</exception>
    public static FpgaConfiguration Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < FixedSize)
        {
            throw new ArgumentException(
                $"An FPGA configuration needs at least {FixedSize} bytes but the payload holds {source.Length}.", nameof(source));
        }

        return new FpgaConfiguration(source[0], source[1], source[2], ZeroTerminatedAscii.Read(source[FixedSize..]));
    }
}
