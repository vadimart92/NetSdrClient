using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Product ID, item 0x0009.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ProductId : IControlItem<ProductId>
{
    public static ushort Code => 0x0009;

    public readonly uint Value;

    public ProductId(uint value)
    {
        Value = value;
    }
}
