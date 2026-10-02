using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>Antenna input selection per channel, item 0x8001. A get request carries only the channel byte.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct AntennaSelect : IControlItem<AntennaSelect>
{
    public static ushort Code => VegaProtocol.AntennaSelectCode;

    public readonly byte Channel;
    public readonly AntennaPort Port;

    public AntennaSelect(byte channel, AntennaPort port)
    {
        Channel = channel;
        Port = port;
    }
}
