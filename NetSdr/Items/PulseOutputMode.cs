using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Pulse output mode, item 0x00B6.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct PulseOutputMode : IControlItem<PulseOutputMode>
{
    public const byte None = 0;
    public const byte RunState = 1;
    public const byte RunPulse = 2;
    public const byte SampleRate = 3;

    public static ushort Code => 0x00B6;

    public readonly byte Channel;
    public readonly byte Mode;

    public PulseOutputMode(byte channel, byte mode)
    {
        Channel = channel;
        Mode = mode;
    }
}
