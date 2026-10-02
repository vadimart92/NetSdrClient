using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Audio frequency gain, item 0x0048.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct AfGain : IControlItem<AfGain>
{
    public static ushort Code => 0x0048;

    public readonly byte Channel;
    public readonly byte Level;

    public AfGain(byte channel, byte level)
    {
        Channel = channel;
        Level = level;
    }
}
