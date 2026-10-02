using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>
/// A control item: a struct that knows its 16-bit item code and how it maps to a frame payload.
/// By default a struct is written and read as its raw little-endian memory image, so a fixed-size item
/// only needs <see cref="Code"/> and <c>[StructLayout(LayoutKind.Sequential, Pack = 1)]</c>.
/// Variable-length items override <see cref="Read"/> and, when they are ever written, <see cref="GetSize"/> and <see cref="Write"/>.
/// </summary>
/// <typeparam name="TSelf">The implementing struct.</typeparam>
public interface IControlItem<TSelf> where TSelf : struct, IControlItem<TSelf>
{
    /// <summary>The 16-bit control item code.</summary>
    static abstract ushort Code { get; }

    /// <summary>Number of payload bytes <see cref="Write"/> produces for <paramref name="item"/>.</summary>
    static virtual int GetSize(in TSelf item) => Unsafe.SizeOf<TSelf>();

    /// <summary>Writes <paramref name="item"/> as payload bytes. <paramref name="destination"/> must hold at least <see cref="GetSize"/> bytes.</summary>
    static virtual void Write(in TSelf item, Span<byte> destination) => MemoryMarshal.Write(destination, in item);

    /// <summary>
    /// Reads an item from payload bytes. The default requires at least <c>sizeof(TSelf)</c> bytes
    /// (otherwise <see cref="ArgumentOutOfRangeException"/>) and ignores any extra trailing bytes.
    /// </summary>
    static virtual TSelf Read(ReadOnlySpan<byte> source) => MemoryMarshal.Read<TSelf>(source);
}
