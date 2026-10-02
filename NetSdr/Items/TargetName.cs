namespace NetSdr.Items;

/// <summary>Target name, item 0x0001: ASCII text with a terminating zero, for example <c>SDR-IP</c>.</summary>
public readonly struct TargetName : IControlItem<TargetName>
{
    private readonly string? _value;

    public static ushort Code => 0x0001;

    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public TargetName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>The name; empty for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>The text plus its terminating zero.</summary>
    public static int GetSize(in TargetName item) => ZeroTerminatedAscii.GetSize(item.Value);

    /// <exception cref="ArgumentException">The name holds a character above 0x7F.</exception>
    public static void Write(in TargetName item, Span<byte> destination) =>
        ZeroTerminatedAscii.Write(item.Value, destination);

    /// <summary>Reads the ASCII text up to the first zero, or to the end of the payload when there is none.</summary>
    public static TargetName Read(ReadOnlySpan<byte> source) => new(ZeroTerminatedAscii.Read(source));
}
