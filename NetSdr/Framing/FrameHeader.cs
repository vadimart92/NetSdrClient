using System.Buffers.Binary;

namespace NetSdr.Framing;

/// <summary>
/// The 2-byte little-endian frame header: bits 15..13 hold the message type,
/// bits 12..0 hold the total frame length including the header.
/// </summary>
public static class FrameHeader
{
    /// <summary>Header size in bytes.</summary>
    public const int Size = 2;

    /// <summary>Largest frame length, reached only by data items that encode it as 0 (8192 payload bytes plus 2 header bytes).</summary>
    public const int MaxLength = 8194;

    /// <summary>Largest frame length expressible in the 13-bit length field.</summary>
    public const int MaxEncodableLength = 8191;

    private const int LengthMask = 0x1FFF;
    private const int TypeShift = 13;
    private const int MaxType = 7;
    private const int FirstDataItemType = 4;

    static FrameHeader()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The NetSDR wire protocol requires a little-endian platform.");
        }
    }

    /// <summary>
    /// Decodes a header. Returns <see langword="false"/> when <paramref name="source"/> holds fewer
    /// than <see cref="Size"/> bytes or the encoded length is invalid (below 2).
    /// </summary>
    /// <param name="source">Bytes starting at the header.</param>
    /// <param name="length">Total frame length including the header; 8194 for a data item that encodes 0.</param>
    /// <param name="type">Raw 3-bit message type; interpret it as <see cref="RequestType"/> or <see cref="ReplyType"/>.</param>
    public static bool TryRead(ReadOnlySpan<byte> source, out int length, out byte type)
    {
        length = 0;
        type = 0;
        if (source.Length < Size)
        {
            return false;
        }

        int raw = BinaryPrimitives.ReadUInt16LittleEndian(source);
        int decodedType = raw >> TypeShift;
        int decodedLength = raw & LengthMask;

        if (decodedLength == 0 && decodedType >= FirstDataItemType)
        {
            decodedLength = MaxLength;
        }
        else if (decodedLength < Size)
        {
            return false;
        }

        length = decodedLength;
        type = (byte)decodedType;
        return true;
    }

    /// <summary>
    /// Encodes a header. A <paramref name="length"/> of 8194 is allowed for data item types (4..7) and is written as 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> is above 7, or <paramref name="length"/> is outside 2..8191
    /// (other than 8194 for a data item type).
    /// </exception>
    public static void Write(Span<byte> destination, int length, byte type)
    {
        if (type > MaxType)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Message type must be in the range 0..7.");
        }

        int encodedLength;
        if (length == MaxLength && type >= FirstDataItemType)
        {
            encodedLength = 0;
        }
        else if (length < Size || length > MaxEncodableLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"Frame length must be in the range {Size}..{MaxEncodableLength}, or {MaxLength} for a data item.");
        }
        else
        {
            encodedLength = length;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)((type << TypeShift) | encodedLength));
    }
}
