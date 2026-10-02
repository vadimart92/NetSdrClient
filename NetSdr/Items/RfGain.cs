using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>RF gain in decibels, item 0x0038.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct RfGain : IControlItem<RfGain>
{
    public static ushort Code => 0x0038;

    public readonly byte Channel;
    public readonly sbyte GainDb;

    public RfGain(byte channel, sbyte gainDb)
    {
        Channel = channel;
        GainDb = gainDb;
    }
}
