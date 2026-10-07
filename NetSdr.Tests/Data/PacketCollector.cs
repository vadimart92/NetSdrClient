using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using NetSdr.Data;

namespace NetSdr.Tests.Data;

/// <summary>A receiver bound to a loopback port that records every delivered packet.</summary>
internal sealed class PacketCollector : IDisposable
{
    private readonly Action<NetSdrDataReceiver>? _onPacket;

    /// <param name="options">Receiver options; the defaults when omitted.</param>
    /// <param name="onPacket">Called from the receive thread after a packet has been added to <see cref="Packets"/>.</param>
    public PacketCollector(DataReceiverOptions? options = null, Action<NetSdrDataReceiver>? onPacket = null)
    {
        _onPacket = onPacket;
        Receiver = new NetSdrDataReceiver(Collect, options);
        try
        {
            Receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Receiver.Start();
            EndPoint = Receiver.LocalEndPoint;
        }
        catch
        {
            Receiver.Dispose();
            throw;
        }
    }

    public NetSdrDataReceiver Receiver { get; }

    public ConcurrentQueue<(DataPacketInfo Info, byte[] Samples)> Packets { get; } = new();

    /// <summary>
    /// The <see cref="Stopwatch.GetTimestamp"/> at which each packet arrived; entry i belongs to entry i of
    /// <see cref="Packets"/>.
    /// </summary>
    public ConcurrentQueue<long> ArrivalTimestamps { get; } = new();

    public IPEndPoint EndPoint { get; }

    public void Dispose() => Receiver.Dispose();

    private void Collect(in DataPacketInfo info, ReadOnlySpan<byte> samples)
    {
        ArrivalTimestamps.Enqueue(Stopwatch.GetTimestamp());
        Packets.Enqueue((info, samples.ToArray()));
        _onPacket?.Invoke(Receiver);
    }
}
