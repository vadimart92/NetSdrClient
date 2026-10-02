using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>D/A converter output mode, item 0x012A.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct DacOutputMode : IControlItem<DacOutputMode>
{
    public const byte None = 0;
    public const byte AdEcho = 1;
    public const byte NcoTrack = 2;
    public const byte Noise = 3;

    public static ushort Code => 0x012A;

    public readonly byte Channel;
    public readonly byte Mode;

    public DacOutputMode(byte channel, byte mode)
    {
        Channel = channel;
        Mode = mode;
    }
}
