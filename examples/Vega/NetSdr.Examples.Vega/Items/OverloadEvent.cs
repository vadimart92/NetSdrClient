using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>Overload notification of one channel, item 0x8004, sent by the receiver unsolicited.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct OverloadEvent : IControlItem<OverloadEvent>
{
    public static ushort Code => VegaProtocol.OverloadEventCode;

    public readonly byte Channel;
    public readonly OverloadedStages Flags;

    public OverloadEvent(byte channel, OverloadedStages flags)
    {
        Channel = channel;
        Flags = flags;
    }
}
