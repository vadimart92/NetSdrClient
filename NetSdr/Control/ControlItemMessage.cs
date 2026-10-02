using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Control;

/// <summary>A frame received from the device, with its payload copied out of the receive buffer.</summary>
public readonly struct ControlItemMessage
{
    public ControlItemMessage(ReplyType type, ushort code, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        Code = code;
        Payload = payload;
    }

    public ReplyType Type { get; }

    /// <summary>The control item code; 0 for data items, acknowledgements and a NAK.</summary>
    public ushort Code { get; }

    /// <summary>The bytes after the item code (after the header for data items and acknowledgements). A copy owned by the consumer.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Whether <see cref="Code"/> is the code of <typeparamref name="T"/>.</summary>
    public bool Is<T>() where T : struct, IControlItem<T> => Code == T.Code;

    /// <summary>Reads <see cref="Payload"/> as a <typeparamref name="T"/>. The item code is not checked; see <see cref="Is{T}"/>.</summary>
    /// <exception cref="NetSdrProtocolException">The payload cannot be read as <typeparamref name="T"/>; the cause is the inner exception.</exception>
    public T As<T>() where T : struct, IControlItem<T> => ReadItem<T>(Payload.Span);

    internal static T ReadItem<T>(ReadOnlySpan<byte> payload) where T : struct, IControlItem<T>
    {
        try
        {
            return T.Read(payload);
        }
        catch (Exception ex)
        {
            throw new NetSdrProtocolException(
                $"Cannot read {typeof(T).Name} (item 0x{T.Code:X4}) from a payload of {payload.Length} bytes: {ex.Message}", ex);
        }
    }
}
