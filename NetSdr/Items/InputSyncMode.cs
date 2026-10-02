using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Input synchronization mode, item 0x00B4.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct InputSyncMode : IControlItem<InputSyncMode>
{
    public const byte None = 0;
    public const byte NegativeEdgeStart = 1;
    public const byte PositiveEdgeStart = 2;
    public const byte LowLevelStart = 3;
    public const byte HighLevelStart = 4;
    public const byte LowLevelMute = 5;
    public const byte HighLevelMute = 6;

    public static ushort Code => 0x00B4;

    public readonly byte Channel;
    public readonly byte Mode;
    public readonly ushort PacketCount;

    public InputSyncMode(byte channel, byte mode, ushort packetCount)
    {
        Channel = channel;
        Mode = mode;
        PacketCount = packetCount;
    }
}
