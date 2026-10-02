namespace NetSdr.Items;

/// <summary>Serial number, item 0x0002: ASCII text with a terminating zero, for example <c>MT123456</c>.</summary>
public readonly struct SerialNumber : IControlItem<SerialNumber>
{
    private readonly string? _value;

    public static ushort Code => 0x0002;

    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public SerialNumber(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>The serial number; empty for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>The text plus its terminating zero.</summary>
    public static int GetSize(in SerialNumber item) => ZeroTerminatedAscii.GetSize(item.Value);

    /// <exception cref="ArgumentException">The serial number holds a character above 0x7F.</exception>
    public static void Write(in SerialNumber item, Span<byte> destination) =>
        ZeroTerminatedAscii.Write(item.Value, destination);

    /// <summary>Reads the ASCII text up to the first zero, or to the end of the payload when there is none.</summary>
    public static SerialNumber Read(ReadOnlySpan<byte> source) => new(ZeroTerminatedAscii.Read(source));
}
