using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>DC offset calibration, item 0x00D0.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct DcCalibration : IControlItem<DcCalibration>
{
    public static ushort Code => 0x00D0;

    public readonly byte Channel;
    public readonly short Offset;

    public DcCalibration(byte channel, short offset)
    {
        Channel = channel;
        Offset = offset;
    }
}
