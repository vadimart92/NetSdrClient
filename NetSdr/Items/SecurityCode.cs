using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Security code, item 0x000B.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SecurityCode : IControlItem<SecurityCode>
{
    public static ushort Code => 0x000B;

    public readonly uint Value;

    public SecurityCode(uint value)
    {
        Value = value;
    }
}
