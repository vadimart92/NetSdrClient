namespace NetSdr.Tests.Items;

public class ControlItemReadTests
{
    [Test]
    public void DefaultRead_ShortSource_Throws() =>
        Assert.That(() => ItemCodec.Read<MyVendorItem>(Hex.Parse("00 2A 00")), Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void DefaultWrite_ProducesRawLittleEndianLayout() =>
        Assert.That(ItemCodec.Write(new MyVendorItem(7, 42)), Is.EqualTo(Hex.Parse("07 2A 00 00 00")));

    [Test]
    public void DefaultRead_LongerSource_ReadsPrefix()
    {
        var item = ItemCodec.Read<MyVendorItem>(Hex.Parse("07 2A 00 00 00 FF FF"));
        Assert.That(item.Channel, Is.EqualTo(7));
        Assert.That(item.Value, Is.EqualTo(42u));
    }
}
