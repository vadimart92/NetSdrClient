using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Receiver channel setup, item 0x0019.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ReceiverChannelSetup : IControlItem<ReceiverChannelSetup>
{
    public const byte SingleChannel = 0;
    public const byte DualChannelSingleAd = 4;

    public static ushort Code => 0x0019;

    public readonly byte Mode;

    public ReceiverChannelSetup(byte mode)
    {
        Mode = mode;
    }
}
