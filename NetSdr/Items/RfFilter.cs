using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>RF filter selection, item 0x0044.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct RfFilter : IControlItem<RfFilter>
{
    public static ushort Code => 0x0044;

    public readonly byte Channel;
    public readonly byte Filter;

    public RfFilter(byte channel, byte filter)
    {
        Channel = channel;
        Filter = filter;
    }

    public RfFilter(byte channel, RfFilterSelection filter) : this(channel, (byte)filter)
    {
    }

    /// <summary><see cref="Filter"/> as an <see cref="RfFilterSelection"/>; the value is not checked against the defined names.</summary>
    public RfFilterSelection Selection => (RfFilterSelection)Filter;
}
