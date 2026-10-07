using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Control;

public sealed partial class ResilientControlClient
{
    /// <summary>
    /// Spec 5.3: what <see cref="ConnectionRestoredContext.Client"/> is. Its requests run on the connection being
    /// restored with the owner's encoding, line discipline and late-reply adoption, but they take no admission (spec 6.2)
    /// and a lost connection fails them with <see cref="IOException"/> at once: they never move to another connection.
    /// Usable until <see cref="End"/>; afterwards every call throws <see cref="InvalidOperationException"/>, and while
    /// the owner is closed <see cref="ObjectDisposedException"/>.
    /// </summary>
    private sealed class RestoreSession(ResilientControlClient owner, Link link) : INetSdrControlClient
    {
        private volatile bool _ended;

        /// <summary>The connection the session is bound to.</summary>
        public Link Link { get; } = link;

        /// <summary>The owner's channel: a callback that reads it takes messages from the application.</summary>
        public ChannelReader<ControlItemMessage> Unsolicited => owner.Unsolicited;

        /// <summary>The owner's completion.</summary>
        public Task Completion => owner.Completion;

        public bool IsConnected => !_ended && Link.Client.IsConnected;

        public IPEndPoint? LocalEndPoint => Link.Client.LocalEndPoint;

        public IPEndPoint? RemoteEndPoint => Link.Client.RemoteEndPoint;

        public Task<T> SetAsync<T>(T item, CancellationToken ct = default) where T : struct, IControlItem<T>
        {
            ThrowIfUnusable();
            return DecodeAsync<T>(owner.CommandAsync(RequestType.Set, T.Code, Encode(in item), typeof(T).Name, ct, this));
        }

        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : struct, IControlItem<T>
        {
            ThrowIfUnusable();
            return owner.RequestAsync<T>(RequestType.Get, ReadOnlySpan<byte>.Empty, null, ct, this);
        }

        public Task<T> GetAsync<T, TKey>(TKey key, CancellationToken ct = default)
            where T : struct, IControlItem<T> where TKey : unmanaged
        {
            ThrowIfUnusable();
            return owner.RequestAsync<T>(RequestType.Get, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), nameof(key), ct, this);
        }

        public Task<T> GetRangeAsync<T, TKey>(TKey key, CancellationToken ct = default)
            where T : struct, IControlItem<T> where TKey : unmanaged
        {
            ThrowIfUnusable();
            return owner.RequestAsync<T>(RequestType.GetRange, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), nameof(key), ct, this);
        }

        public Task<ControlItemMessage> SendAsync(
            RequestType type, ushort code, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            ThrowIfUnusable();
            return owner.CommandAsync(type, code, CopyPayload(type, payload), null, ct, this);
        }

        /// <summary>Does nothing: the connection belongs to the owner, so a device object wrapped around the session can be disposed inside the callback.</summary>
        public ValueTask DisposeAsync() => default;

        /// <summary>Called once the callback's task has completed; from then on every call throws.</summary>
        public void End() => _ended = true;

        /// <exception cref="InvalidOperationException">The callback has completed.</exception>
        public void ThrowIfEnded()
        {
            if (_ended)
            {
                throw new InvalidOperationException(
                    "ConnectionRestoredContext.Client can be used only until the ConnectionRestored callback completes.");
            }
        }

        /// <summary>Spec 5.3: the end of the session first, then the closing of the owner.</summary>
        private void ThrowIfUnusable()
        {
            ThrowIfEnded();
            owner.ThrowIfClosed();
        }
    }

    /// <summary>
    /// Spec 7.5: the marker of a running <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback, which
    /// flows to everything the callback starts through <see cref="_restoreScope"/>. It is a mutable object, not a value:
    /// a task the callback started without awaiting sees <see cref="Active"/> turn false once the callback has returned,
    /// and may use the client normally from then on.
    /// </summary>
    private sealed class RestoreScope(ResilientControlClient owner)
    {
        private volatile bool _active;

        public ResilientControlClient Owner { get; } = owner;

        /// <summary>Whether the callback is still running; a command of the owner inside it is a reentrant call.</summary>
        public bool Active
        {
            get => _active;
            set => _active = value;
        }

        /// <summary>The first reentrant call's exception, however the callback ended; it makes the client give up.</summary>
        public Exception? Reentered { get; set; }
    }

    /// <summary>
    /// Spec 7.5: the callback called the <see cref="ResilientControlClient"/> instead of <c>context.Client</c>. The
    /// reconnection pipeline never retries it: the client gives up at once with the reentrancy error as the cause (spec 7.6).
    /// </summary>
    private sealed class FatalRestoreException(Exception reentrancy)
        : Exception("ConnectionRestored called the ResilientControlClient instead of context.Client.", reentrancy);
}
