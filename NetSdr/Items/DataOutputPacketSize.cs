using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Data output packet size, item 0x00C4.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct DataOutputPacketSize : IControlItem<DataOutputPacketSize>
{
    /// <summary>Large data packets.</summary>
    public const byte Large = 0;

    /// <summary>Small data packets.</summary>
    public const byte Small = 1;

    public static ushort Code => 0x00C4;

    public readonly byte Size;

    public DataOutputPacketSize(byte size)
    {
        Size = size;
    }
}
