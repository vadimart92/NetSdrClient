using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Tests.Items;

/// <summary>A custom item defined the way an application would: code plus fixed layout, no registration.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct MyVendorItem : IControlItem<MyVendorItem>
{
    public static ushort Code => 0x0150;

    public readonly byte Channel;
    public readonly uint Value;

    public MyVendorItem(byte channel, uint value)
    {
        Channel = channel;
        Value = value;
    }
}
