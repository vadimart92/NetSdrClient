using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>Vega firmware version, item 0x8005; the receiver clients use it to choose the board temperature layout.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct VegaFirmwareInfo : IControlItem<VegaFirmwareInfo>
{
    public static ushort Code => VegaProtocol.FirmwareInfoCode;

    public readonly ushort Version;

    public VegaFirmwareInfo(ushort version)
    {
        Version = version;
    }
}
