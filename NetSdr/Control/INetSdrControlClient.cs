using System.Net;
using System.Threading.Channels;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Control;

/// <summary>
/// The NetSDR control channel. One request is in flight at a time, and replies arrive in the order of the calls.
/// Implemented by <see cref="NetSdrControlClient"/> and <c>ResilientControlClient</c>.
/// </summary>
public interface INetSdrControlClient : IAsyncDisposable
{
    /// <summary>Sets a control item and returns the item the device echoes back.</summary>
    Task<T> SetAsync<T>(T item, CancellationToken ct = default) where T : struct, IControlItem<T>;

    /// <summary>Requests a control item that needs no key.</summary>
    Task<T> GetAsync<T>(CancellationToken ct = default) where T : struct, IControlItem<T>;

    /// <summary>Requests a control item identified by <paramref name="key"/>, sent as its raw little-endian bytes.</summary>
    Task<T> GetAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged;

    /// <summary>Requests the range of a control item; the device answers with a <c>RangeResponse</c>.</summary>
    Task<T> GetRangeAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged;

    /// <summary>Sends a request for any item code and returns the device's reply uninterpreted.</summary>
    Task<ControlItemMessage> SendAsync(RequestType type, ushort code, ReadOnlyMemory<byte> payload,
        CancellationToken ct = default);

    /// <summary>
    /// Everything the device sends other than a reply to a request. Completes without error only when the client is
    /// unusable for good, and only after <see cref="Completion"/> has its outcome.
    /// </summary>
    ChannelReader<ControlItemMessage> Unsolicited { get; }

    /// <summary>
    /// Completes successfully after <see cref="IAsyncDisposable.DisposeAsync"/>, and with the cause when the client
    /// becomes unusable for good: the plain client faulted, or the resilient one gave up reconnecting.
    /// </summary>
    Task Completion { get; }

    /// <summary>Whether there is a live, usable connection right now.</summary>
    bool IsConnected { get; }

    /// <summary>The local end of the current connection, or of the last one until there is a new one.</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>The device's end of the current connection, or of the last one until there is a new one.</summary>
    IPEndPoint? RemoteEndPoint { get; }
}
