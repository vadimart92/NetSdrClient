namespace NetSdr.Tests.Items;

public class ControlItemReadTests
{
    [Fact]
    public void DefaultRead_ShortSource_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemCodec.Read<MyVendorItem>(Hex.Parse("00 2A 00")));

    [Fact]
    public void DefaultWrite_ProducesRawLittleEndianLayout() =>
        Assert.Equal(Hex.Parse("07 2A 00 00 00"), ItemCodec.Write(new MyVendorItem(7, 42)));

    [Fact]
    public void DefaultRead_LongerSource_ReadsPrefix()
    {
        var item = ItemCodec.Read<MyVendorItem>(Hex.Parse("07 2A 00 00 00 FF FF"));
        Assert.Equal(7, item.Channel);
        Assert.Equal(42u, item.Value);
    }
}
