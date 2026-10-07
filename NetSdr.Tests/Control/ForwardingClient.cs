using System.Net;
using System.Threading.Channels;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Tests.Control;

/// <summary>
/// An <see cref="INetSdrControlClient"/> that is not a <see cref="NetSdrControlClient"/>: it counts the requests it is
/// given and forwards everything to <paramref name="inner"/>.
/// </summary>
internal sealed class ForwardingClient(INetSdrControlClient inner) : INetSdrControlClient
{
    /// <summary>The number of requests made through this client.</summary>
    public int Requests;

    public Task<T> SetAsync<T>(T item, CancellationToken ct = default) where T : struct, IControlItem<T>
    {
        Interlocked.Increment(ref Requests);
        return inner.SetAsync(item, ct);
    }

    public Task<T> GetAsync<T>(CancellationToken ct = default) where T : struct, IControlItem<T>
    {
        Interlocked.Increment(ref Requests);
        return inner.GetAsync<T>(ct);
    }

    public Task<T> GetAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged
    {
        Interlocked.Increment(ref Requests);
        return inner.GetAsync<T, TKey>(key, ct);
    }

    public Task<T> GetRangeAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged
    {
        Interlocked.Increment(ref Requests);
        return inner.GetRangeAsync<T, TKey>(key, ct);
    }

    public Task<ControlItemMessage> SendAsync(
        RequestType type, ushort code, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Requests);
        return inner.SendAsync(type, code, payload, ct);
    }

    public ChannelReader<ControlItemMessage> Unsolicited => inner.Unsolicited;

    public Task Completion => inner.Completion;

    public bool IsConnected => inner.IsConnected;

    public IPEndPoint? LocalEndPoint => inner.LocalEndPoint;

    public IPEndPoint? RemoteEndPoint => inner.RemoteEndPoint;

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
