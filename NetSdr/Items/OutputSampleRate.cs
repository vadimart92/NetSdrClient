using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Output (I/Q) data sample rate in hertz, item 0x00B8.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct OutputSampleRate : IControlItem<OutputSampleRate>
{
    public static ushort Code => 0x00B8;

    public readonly byte Channel;
    public readonly uint Hz;

    public OutputSampleRate(byte channel, uint hz)
    {
        Channel = channel;
        Hz = hz;
    }
}
