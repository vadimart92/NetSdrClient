using System.Text;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>Device label, item 0x8003: ASCII text of at most <see cref="VegaProtocol.MaxLabelLength"/> characters with a terminating zero.</summary>
public readonly struct DeviceLabel : IControlItem<DeviceLabel>
{
    private readonly string? _value;

    public static ushort Code => VegaProtocol.DeviceLabelCode;

    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The label is longer than <see cref="VegaProtocol.MaxLabelLength"/> characters or holds a character above 0x7F.
    /// </exception>
    public DeviceLabel(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > VegaProtocol.MaxLabelLength)
        {
            throw new ArgumentException(
                $"The label is longer than {VegaProtocol.MaxLabelLength} characters.", nameof(value));
        }

        if (!Ascii.IsValid(value))
        {
            throw new ArgumentException("The label holds a character that is not ASCII.", nameof(value));
        }

        _value = value;
    }

    // Reading does not validate: the limits protect what the host sends, not what a device reports. A label that
    // is read this way is still checked by Write, so it cannot be sent back over-long.
    // The unused parameter only tells this constructor apart from the validating public one.
    private DeviceLabel(string value, bool fromWire)
    {
        _value = value;
    }

    /// <summary>The label; empty for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>The text plus its terminating zero.</summary>
    public static int GetSize(in DeviceLabel item) => item.Value.Length + 1;

    /// <exception cref="ArgumentException">
    /// The label is longer than <see cref="VegaProtocol.MaxLabelLength"/> characters, which only a label read from
    /// a device can be, or holds a character above 0x7F.
    /// </exception>
    public static void Write(in DeviceLabel item, Span<byte> destination)
    {
        string value = item.Value;
        if (value.Length > VegaProtocol.MaxLabelLength)
        {
            throw new ArgumentException(
                $"The label is longer than {VegaProtocol.MaxLabelLength} characters.", nameof(item));
        }

        if (!Ascii.IsValid(value))
        {
            throw new ArgumentException("The label holds a character that is not ASCII.", nameof(item));
        }

        Encoding.ASCII.GetBytes(value, destination);
        destination[value.Length] = 0;
    }

    /// <summary>Reads the ASCII text up to the first zero, or to the end of the payload when there is none.</summary>
    public static DeviceLabel Read(ReadOnlySpan<byte> source)
    {
        int end = source.IndexOf((byte)0);
        return new DeviceLabel(Encoding.ASCII.GetString(end < 0 ? source : source[..end]), fromWire: true);
    }
}
