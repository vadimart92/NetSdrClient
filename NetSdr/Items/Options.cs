using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Hardware options, item 0x000A.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct Options : IControlItem<Options>
{
    /// <summary>Flag in <see cref="Flags"/>: the reference lock board is installed.</summary>
    public const byte ReflockBoard = 0x02;

    /// <summary>Flag in <see cref="Flags"/>: the down converter board is installed.</summary>
    public const byte DownConverterBoard = 0x04;

    public static ushort Code => 0x000A;

    public readonly byte Flags;
    public readonly byte Custom;
    public readonly uint Detail;

    public Options(byte flags, byte custom, uint detail)
    {
        Flags = flags;
        Custom = custom;
        Detail = detail;
    }
}
