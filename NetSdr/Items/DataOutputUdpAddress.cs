using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Destination IP address and port for UDP data output, item 0x00C5.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct DataOutputUdpAddress : IControlItem<DataOutputUdpAddress>
{
    public static ushort Code => 0x00C5;

    /// <summary>The address bytes read as a big-endian number, so 192.168.3.123 is <c>0xC0A8037B</c>.</summary>
    public readonly uint Ip;

    public readonly ushort Port;

    public DataOutputUdpAddress(uint ip, ushort port)
    {
        Ip = ip;
        Port = port;
    }

    /// <summary>Builds the item for an IPv4 end point.</summary>
    /// <remarks>
    /// The address must be one the device can reach. The local end point of a data receiver that is bound to
    /// <see cref="IPAddress.Any"/> is <c>0.0.0.0</c>, and a real device cannot send to that. Build the end point from
    /// the address of the control connection and the port of the receiver:
    /// <c>new IPEndPoint(client.LocalEndPoint.Address, receiver.LocalEndPoint.Port)</c>;
    /// see <see cref="Control.NetSdrControlClient.LocalEndPoint"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="endPoint"/> is not an IPv4 address.</exception>
    public static DataOutputUdpAddress For(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        if (endPoint.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 end points are supported.", nameof(endPoint));
        }

        Span<byte> bytes = stackalloc byte[4];
        endPoint.Address.TryWriteBytes(bytes, out _);
        return new DataOutputUdpAddress(BinaryPrimitives.ReadUInt32BigEndian(bytes), (ushort)endPoint.Port);
    }

    /// <summary>The address and port as an end point.</summary>
    public IPEndPoint ToEndPoint()
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, Ip);
        return new IPEndPoint(new IPAddress(bytes), Port);
    }
}
