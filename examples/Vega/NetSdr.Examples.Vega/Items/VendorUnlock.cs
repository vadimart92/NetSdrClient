using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>Vendor unlock key, item 0x8000; the Vega extension items are rejected until the key is accepted.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct VendorUnlock : IControlItem<VendorUnlock>
{
    public static ushort Code => VegaProtocol.VendorUnlockCode;

    public readonly uint Key;

    public VendorUnlock(uint key)
    {
        Key = key;
    }
}
