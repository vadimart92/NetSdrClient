using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>Overload notification of one channel, item 0x8004, sent by the receiver unsolicited.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct OverloadEvent : IControlItem<OverloadEvent>
{
    public static ushort Code => VegaProtocol.OverloadEventCode;

    public readonly byte Channel;
    public readonly OverloadFlags Flags;

    public OverloadEvent(byte channel, OverloadFlags flags)
    {
        Channel = channel;
        Flags = flags;
    }
}
