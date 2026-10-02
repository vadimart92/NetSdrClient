using NetSdr.Items;

namespace NetSdr.Tests.Items;

/// <summary>Exercises an item's payload codec directly, without framing.</summary>
internal static class ItemCodec
{
    public static T Read<T>(byte[] payload) where T : struct, IControlItem<T> => T.Read(payload);

    public static byte[] Write<T>(in T item) where T : struct, IControlItem<T>
    {
        var payload = new byte[T.GetSize(in item)];
        T.Write(in item, payload);
        return payload;
    }
}
