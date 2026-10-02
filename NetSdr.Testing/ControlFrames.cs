using System.Buffers.Binary;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Testing;

/// <summary>Builds and parses whole control frames (header, item code, payload) for tests.</summary>
public static class ControlFrames
{
    private const int CodeSize = 2;
    private const int PrefixSize = FrameHeader.Size + CodeSize;

    /// <summary>Builds a frame from a raw header type, item code and payload.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The type is above 7 or the frame would exceed the maximum length.</exception>
    public static byte[] Encode(byte type, ushort code, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[PrefixSize + payload.Length];
        FrameHeader.Write(frame, frame.Length, type);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(FrameHeader.Size), code);
        payload.CopyTo(frame.AsSpan(PrefixSize));
        return frame;
    }

    /// <summary>Builds a host-to-device frame carrying <paramref name="item"/> as its payload.</summary>
    public static byte[] Request<T>(RequestType type, in T item) where T : struct, IControlItem<T> =>
        EncodeItem((byte)type, in item);

    /// <summary>Builds a device-to-host frame carrying <paramref name="item"/> as its payload.</summary>
    public static byte[] Reply<T>(ReplyType type, in T item) where T : struct, IControlItem<T> =>
        EncodeItem((byte)type, in item);

    /// <summary>
    /// Reads the item out of a complete control frame. The message type is not checked.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The header is invalid, its length differs from <c>frame.Length</c>, or the item code is not <c>T.Code</c>.
    /// </exception>
    public static T Decode<T>(ReadOnlySpan<byte> frame) where T : struct, IControlItem<T>
    {
        if (!FrameHeader.TryRead(frame, out int length, out _))
        {
            throw new ArgumentException("The frame does not start with a valid header.", nameof(frame));
        }

        if (length != frame.Length)
        {
            throw new ArgumentException(
                $"The header declares {length} bytes but the frame holds {frame.Length}.", nameof(frame));
        }

        if (frame.Length < PrefixSize)
        {
            throw new ArgumentException("The frame is too short to hold an item code.", nameof(frame));
        }

        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(frame[FrameHeader.Size..]);
        if (code != T.Code)
        {
            throw new ArgumentException(
                $"The frame carries item code 0x{code:X4} but {typeof(T).Name} has code 0x{T.Code:X4}.", nameof(frame));
        }

        return T.Read(frame[PrefixSize..]);
    }

    private static byte[] EncodeItem<T>(byte type, in T item) where T : struct, IControlItem<T>
    {
        var frame = new byte[PrefixSize + T.GetSize(in item)];
        FrameHeader.Write(frame, frame.Length, type);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(FrameHeader.Size), T.Code);
        T.Write(in item, frame.AsSpan(PrefixSize));
        return frame;
    }
}
