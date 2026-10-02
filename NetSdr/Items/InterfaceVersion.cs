using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Interface (protocol) version number, item 0x0003.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct InterfaceVersion : IControlItem<InterfaceVersion>
{
    public static ushort Code => 0x0003;

    public readonly ushort Version;

    public InterfaceVersion(ushort version)
    {
        Version = version;
    }
}
