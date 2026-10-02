using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>A/D input sample rate in hertz, item 0x00B0.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct AdInputSampleRate : IControlItem<AdInputSampleRate>
{
    public static ushort Code => 0x00B0;

    public readonly byte Channel;
    public readonly uint Hz;

    public AdInputSampleRate(byte channel, uint hz)
    {
        Channel = channel;
        Hz = hz;
    }
}
