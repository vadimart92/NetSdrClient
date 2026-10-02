using System.Runtime.InteropServices;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Testing;

/// <summary>A control request the test server received from the client.</summary>
public readonly struct ControlRequest
{
    internal ControlRequest(RequestType type, ushort code, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        Code = code;
        Payload = payload;
    }

    public RequestType Type { get; }

    /// <summary>The control item code.</summary>
    public ushort Code { get; }

    /// <summary>The bytes after the item code: the item for a Set, the key for a Get or GetRange.</summary>
    public ReadOnlyMemory<byte> Payload { get; }
}

/// <summary>A control request for the item <typeparamref name="T"/>, as handed to a typed handler.</summary>
/// <typeparam name="T">The control item the request is about.</typeparam>
public readonly struct ControlRequest<T> where T : struct, IControlItem<T>
{
    internal ControlRequest(RequestType type, T item, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        Item = item;
        Payload = payload;
    }

    public RequestType Type { get; }

    /// <summary>The item the client set; <c>default</c> for a Get or GetRange.</summary>
    public T Item { get; }

    /// <summary>The bytes after the item code: the item for a Set, the key for a Get or GetRange.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Reads the start of <see cref="Payload"/> as the key of a Get or GetRange, for example a channel number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The payload is shorter than <typeparamref name="TKey"/>.</exception>
    public TKey Key<TKey>() where TKey : unmanaged => MemoryMarshal.Read<TKey>(Payload.Span);
}
