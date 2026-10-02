using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NetSdr.Tests.Data;

/// <summary>Builds and sends data-channel datagrams for receiver tests.</summary>
internal static class UdpTestSender
{
    private const int HeaderSize = 4;
    private const int TypeShift = 13;
    private const int MaxEncodableLength = 8191;

    /// <summary>Sends <paramref name="datagram"/> to <paramref name="target"/> from 127.0.0.1.</summary>
    public static void Send(IPEndPoint target, byte[] datagram)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.SendTo(datagram, target);
    }

    /// <summary>
    /// Builds a data datagram: a 2-byte header of <paramref name="type"/> declaring <paramref name="headerLength"/>
    /// (the total length when omitted; a length above 8191 is written as 0), the 2-byte sequence number, and a
    /// payload whose byte <c>i</c> is <c>(byte)i</c>.
    /// </summary>
    public static byte[] Datagram(ushort sequence, int totalLength, byte type = 4, int? headerLength = null)
    {
        var datagram = new byte[totalLength];
        int declared = headerLength ?? totalLength;
        int encoded = declared > MaxEncodableLength ? 0 : declared;
        BinaryPrimitives.WriteUInt16LittleEndian(datagram, (ushort)((type << TypeShift) | encoded));
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(2), sequence);
        for (int i = 0; i < totalLength - HeaderSize; i++)
        {
            datagram[HeaderSize + i] = (byte)i;
        }

        return datagram;
    }
}
