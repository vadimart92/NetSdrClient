using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>A/D converter modes, item 0x008A. <see cref="Flags"/> combines the flag constants.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct AdModes : IControlItem<AdModes>
{
    /// <summary>Flag: dither enabled.</summary>
    public const byte Dither = 0x01;

    /// <summary>Flag: A/D gain 1.5 enabled.</summary>
    public const byte Gain1_5 = 0x02;

    public static ushort Code => 0x008A;

    public readonly byte Channel;
    public readonly byte Flags;

    public AdModes(byte channel, byte flags)
    {
        Channel = channel;
        Flags = flags;
    }
}
