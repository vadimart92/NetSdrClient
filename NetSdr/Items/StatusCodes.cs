namespace NetSdr.Items;

/// <summary>
/// Status or error codes, item 0x0005. The payload is a list of one-byte codes; the device sends this item
/// unsolicited when its state changes. Written too, so an emulated device can answer with it or send it.
/// </summary>
public readonly struct StatusCodes : IControlItem<StatusCodes>
{
    public const byte Idle = 0x0B;
    public const byte Busy = 0x0C;
    public const byte LoadingParameters = 0x0D;
    public const byte BootIdle = 0x0E;
    public const byte BootBusy = 0x0F;
    public const byte AdOverload = 0x20;
    public const byte BootError = 0x80;

    private readonly byte[]? _codes;

    public static ushort Code => 0x0005;

    /// <exception cref="ArgumentNullException"><paramref name="codes"/> is <see langword="null"/>.</exception>
    public StatusCodes(byte[] codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        _codes = codes;
    }

    /// <summary>Every byte of the payload, in order; empty for <c>default</c>.</summary>
    public byte[] Codes => _codes ?? [];

    /// <summary>One byte per code.</summary>
    public static int GetSize(in StatusCodes item) => item.Codes.Length;

    /// <summary>Writes every code as one byte, in order.</summary>
    public static void Write(in StatusCodes item, Span<byte> destination) => item.Codes.CopyTo(destination);

    /// <summary>Reads every payload byte as a code.</summary>
    public static StatusCodes Read(ReadOnlySpan<byte> source) => new(source.ToArray());
}
