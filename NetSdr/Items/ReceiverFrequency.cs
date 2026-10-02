using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Receiver frequency in hertz, item 0x0020.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ReceiverFrequency : IControlItem<ReceiverFrequency>
{
    public const byte Channel1 = 0;

    /// <summary>The display frequency.</summary>
    public const byte Display = 1;

    public const byte Channel2 = 2;
    public const byte All = 0xFF;

    public static ushort Code => 0x0020;

    public readonly byte Channel;
    public readonly UInt40 Hz;

    public ReceiverFrequency(byte channel, UInt40 hz)
    {
        Channel = channel;
        Hz = hz;
    }
}
