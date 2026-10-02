using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Testing;

/// <summary>
/// Emulates a NetSDR receiver over real sockets on loopback, for the tests of this framework and of applications built on it.
/// It answers and records requests; the checks are left to the test. One client is served at a time.
/// </summary>
public sealed partial class NetSdrTestServer : IAsyncDisposable
{
    private const int CodeSize = 2;
    private const int FramePrefixSize = FrameHeader.Size + CodeSize;

    /// <summary>How many payloads the server remembers per item code.</summary>
    private const int StatePerCode = 16;

    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _clientConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The next four are guarded by _sync. Handlers always run outside it, so a handler may call back into the server.
    private readonly Dictionary<ushort, Func<ControlRequest, ControlReply>> _handlers = [];
    private readonly Dictionary<ushort, List<byte[]>> _state = [];
    private readonly List<ControlRequest> _received = [];
    private Connection? _client;

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _disposal;

    /// <summary>The TCP port the server listens on; 0 until <see cref="StartAsync"/> has been called.</summary>
    public int Port { get; private set; }

    /// <summary>Completes when the first client connects, and is cancelled if the server is disposed before that.</summary>
    public Task ClientConnected => _clientConnected.Task;

    /// <summary>A snapshot of every control request received so far, oldest first.</summary>
    public IReadOnlyList<ControlRequest> Received
    {
        get
        {
            lock (_sync)
            {
                return _received.ToArray();
            }
        }
    }

    /// <summary>Starts listening on loopback.</summary>
    /// <param name="port">The port to listen on; 0 lets the system choose, see <see cref="Port"/>.</param>
    /// <exception cref="InvalidOperationException">The server has already been started.</exception>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    public Task StartAsync(int port = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_listener is not null)
            {
                throw new InvalidOperationException("The server has already been started.");
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the handler for the item <typeparamref name="T"/>, replacing an earlier one for the same code. It runs
    /// instead of the default behaviour: for a Set <see cref="ControlRequest{T}.Item"/> holds the item, for a Get or
    /// GetRange the key is read with <see cref="ControlRequest{T}.Key{TKey}"/>. An exception from the handler is answered with a NAK.
    /// </summary>
    public void OnRequest<T>(Func<ControlRequest<T>, ControlReply> handler) where T : struct, IControlItem<T>
    {
        ArgumentNullException.ThrowIfNull(handler);
        OnRequest(T.Code, request => handler(new ControlRequest<T>(
            request.Type,
            request.Type == RequestType.Set ? T.Read(request.Payload.Span) : default,
            request.Payload)));
    }

    /// <summary>
    /// Sets the handler for an item code, replacing an earlier one for the same code. It runs on the connection's
    /// thread, one request at a time. An exception from the handler is answered with a NAK.
    /// </summary>
    public void OnRequest(ushort code, Func<ControlRequest, ControlReply> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _handlers[code] = handler;
        }
    }

    /// <summary>Adds <paramref name="item"/> to the state a Get is answered from, as if the client had set it.</summary>
    public void Preload<T>(T item) where T : struct, IControlItem<T>
    {
        var payload = new byte[T.GetSize(in item)];
        T.Write(in item, payload);
        lock (_sync)
        {
            AddState(T.Code, payload);
        }
    }

    /// <summary>Sends an <c>Unsolicited</c> frame carrying <paramref name="item"/> to the client.</summary>
    /// <exception cref="InvalidOperationException">No client is connected.</exception>
    public Task SendUnsolicitedAsync<T>(T item) where T : struct, IControlItem<T> =>
        SendFrameAsync(ControlFrames.Reply(ReplyType.Unsolicited, in item));

    /// <summary>Sends an <c>Unsolicited</c> frame with an arbitrary item code and payload to the client.</summary>
    /// <exception cref="InvalidOperationException">No client is connected.</exception>
    public Task SendUnsolicitedAsync(ushort code, ReadOnlyMemory<byte> payload) =>
        SendFrameAsync(ControlFrames.Encode((byte)ReplyType.Unsolicited, code, payload.Span));

    /// <summary>Closes the connection of the client being served, as if the link dropped. Does nothing without a client.</summary>
    public Task DisconnectClientAsync()
    {
        Connection? client;
        lock (_sync)
        {
            client = _client;
        }

        client?.Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops listening, closes the client's connection and stops the data stream. If a stream ended with an exception from
    /// <see cref="StreamOptions.Source"/> or <see cref="StreamOptions.DropPacket"/>, it is thrown here.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposal ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task? acceptLoop;
        Connection? client;
        TcpListener? listener;
        lock (_sync)
        {
            acceptLoop = _acceptLoop;
            client = _client;
            listener = _listener;
        }

        // Cancel before closing anything, so the accept loop takes the failures that follow for the shutdown.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _clientConnected.TrySetCanceled();
        listener?.Stop();
        client?.Close();
        try
        {
            if (acceptLoop is not null)
            {
                await acceptLoop.ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                // A stream started by hand does not end with the client, so it is stopped here.
                await StopStreamingAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifetime.Dispose();
            }
        }

        ThrowStreamFault();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        CancellationToken token = _lifetime.Token;
        try
        {
            while (true)
            {
                Socket socket = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
                await ServeAsync(socket).ConfigureAwait(false);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // The server is being disposed.
        }
    }

    /// <summary>Serves one client until it leaves or is disconnected. A broken connection ends only this client.</summary>
    private async Task ServeAsync(Socket socket)
    {
        Connection connection;
        try
        {
            connection = new Connection(socket, _lifetime.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The client dropped before it could be served; wait for the next one.
            socket.Dispose();
            return;
        }

        lock (_sync)
        {
            _client = connection;
        }

        // The client is registered first: whoever waits for the connection may send an unsolicited frame right away.
        _clientConnected.TrySetResult();
        try
        {
            await ReadRequestsAsync(connection).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client left, was disconnected or the server is shutting down.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_client, connection))
                {
                    _client = null;
                }
            }

            connection.Dispose();

            // The stream this client started ends with it.
            await EndStreamOfAsync(connection).ConfigureAwait(false);
        }
    }

    private async Task ReadRequestsAsync(Connection connection)
    {
        PipeReader reader = PipeReader.Create(connection.Stream);
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(connection.Token).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                FrameStatus status;
                byte[] frame;
                byte type;
                while ((status = TryTakeFrame(ref buffer, out frame, out type)) == FrameStatus.Frame)
                {
                    await HandleFrameAsync(connection, frame, type).ConfigureAwait(false);
                }

                if (status == FrameStatus.Invalid)
                {
                    return;
                }

                // Everything buffered has been examined, so the next read waits for more bytes.
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return;
                }
            }
        }
        finally
        {
            reader.Complete();
        }
    }

    private enum FrameStatus
    {
        NeedMore,
        Frame,
        Invalid,
    }

    /// <summary>Splits the next whole frame off <paramref name="buffer"/> and copies it out.</summary>
    private static FrameStatus TryTakeFrame(ref ReadOnlySequence<byte> buffer, out byte[] frame, out byte type)
    {
        frame = [];
        type = 0;
        if (buffer.Length < FrameHeader.Size)
        {
            return FrameStatus.NeedMore;
        }

        Span<byte> header = stackalloc byte[FrameHeader.Size];
        buffer.Slice(0, FrameHeader.Size).CopyTo(header);
        if (!FrameHeader.TryRead(header, out int length, out type))
        {
            return FrameStatus.Invalid;
        }

        if (buffer.Length < length)
        {
            return FrameStatus.NeedMore;
        }

        frame = buffer.Slice(0, length).ToArray();
        buffer = buffer.Slice(length);
        return FrameStatus.Frame;
    }

    private async Task HandleFrameAsync(Connection connection, byte[] frame, byte type)
    {
        if (type > (byte)RequestType.GetRange)
        {
            // Acknowledgements and data items from the client are not control requests.
            return;
        }

        if (frame.Length < FramePrefixSize)
        {
            // Without an item code there is nothing to record or look up.
            await connection.WriteAsync(ControlReply.NakFrameBytes).ConfigureAwait(false);
            return;
        }

        var request = new ControlRequest(
            (RequestType)type,
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(FrameHeader.Size)),
            frame.AsMemory(FramePrefixSize));
        lock (_sync)
        {
            _received.Add(request);
        }

        ControlReply reply;
        ReadOnlyMemory<byte> replyFrame = default;
        Func<Task>? afterReply = null;
        try
        {
            Dispatched dispatched = await DispatchAsync(request, connection).ConfigureAwait(false);
            reply = dispatched.Reply;
            afterReply = dispatched.AfterReply;
            if (!reply.IsSilent)
            {
                replyFrame = reply.ToFrame(in request);
            }
        }
        catch (Exception)
        {
            // A failing handler, or a reply that cannot be encoded, is the device rejecting the request.
            reply = ControlReply.Nak;
            replyFrame = ControlReply.NakFrameBytes;
            afterReply = null;
        }

        if (reply.IsSilent)
        {
            return;
        }

        if (reply.Delay > TimeSpan.Zero)
        {
            await Task.Delay(reply.Delay, connection.Token).ConfigureAwait(false);
        }

        await connection.WriteAsync(replyFrame).ConfigureAwait(false);
        if (afterReply is not null)
        {
            await afterReply().ConfigureAwait(false);
        }
    }

    /// <summary>A reply, and work that must wait until the reply has been written.</summary>
    private readonly record struct Dispatched(ControlReply Reply, Func<Task>? AfterReply = null);

    /// <summary>
    /// Chooses the reply to a request: the handler for its code if there is one, then the receiver state when
    /// <see cref="AutoStream"/> acts on it, otherwise the default behaviour from the state.
    /// </summary>
    private async ValueTask<Dispatched> DispatchAsync(ControlRequest request, Connection connection)
    {
        Func<ControlRequest, ControlReply>? handler;
        lock (_sync)
        {
            _handlers.TryGetValue(request.Code, out handler);
        }

        if (handler is not null)
        {
            return new Dispatched(handler(request));
        }

        if (IsAutoStreamRequest(request))
        {
            return await ReplyToReceiverStateAsync(request, connection).ConfigureAwait(false);
        }

        return new Dispatched(ReplyFromState(request));
    }

    /// <summary>Set stores the payload and echoes it, Get answers from the stored payloads, GetRange is rejected.</summary>
    private ControlReply ReplyFromState(ControlRequest request)
    {
        switch (request.Type)
        {
            case RequestType.Set:
                StoreState(request);
                return ControlReply.Echo;
            case RequestType.Get:
                return TryGetState(request.Code, request.Payload.Span, out byte[] payload)
                    ? ControlReply.Bytes(payload)
                    : ControlReply.Nak;
            default:
                return ControlReply.Nak;
        }
    }

    /// <summary>Stores the payload of a Set as the newest of its code.</summary>
    private void StoreState(ControlRequest request)
    {
        lock (_sync)
        {
            AddState(request.Code, request.Payload.ToArray());
        }
    }

    /// <summary>Adds a payload as the newest of its code and forgets the oldest beyond <see cref="StatePerCode"/>. Call under the lock.</summary>
    private void AddState(ushort code, byte[] payload)
    {
        if (!_state.TryGetValue(code, out List<byte[]>? payloads))
        {
            payloads = [];
            _state[code] = payloads;
        }

        if (payloads.Count == StatePerCode)
        {
            payloads.RemoveAt(0);
        }

        payloads.Add(payload);
    }

    /// <summary>Finds the newest payload of <paramref name="code"/> that starts with <paramref name="key"/>; an empty key matches any.</summary>
    private bool TryGetState(ushort code, ReadOnlySpan<byte> key, out byte[] payload)
    {
        lock (_sync)
        {
            if (_state.TryGetValue(code, out List<byte[]>? payloads))
            {
                for (int i = payloads.Count - 1; i >= 0; i--)
                {
                    if (payloads[i].AsSpan().StartsWith(key))
                    {
                        payload = payloads[i];
                        return true;
                    }
                }
            }
        }

        payload = [];
        return false;
    }

    /// <summary>Finds the newest payload of <paramref name="code"/>, whatever its key.</summary>
    private bool TryGetLatestState(ushort code, out byte[] payload) => TryGetState(code, [], out payload);

    private async Task SendFrameAsync(byte[] frame)
    {
        Connection? client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            client = _client;
        }

        if (client is null)
        {
            throw new InvalidOperationException("No client is connected.");
        }

        try
        {
            await client.WriteAsync(frame).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException("The client has disconnected.");
        }
    }

    /// <summary>One accepted client: its stream, a token that ends with it, and the lock that keeps whole frames apart on the wire.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly CancellationTokenSource _closed;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Connection(Socket socket, CancellationToken serverToken)
        {
            socket.NoDelay = true;
            RemoteEndPoint = (IPEndPoint?)socket.RemoteEndPoint;
            Stream = new NetworkStream(socket, ownsSocket: true);
            _closed = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            Token = _closed.Token;
        }

        public NetworkStream Stream { get; }

        public IPEndPoint? RemoteEndPoint { get; }

        /// <summary>Cancelled when the connection is closed or the server is disposed.</summary>
        public CancellationToken Token { get; }

        /// <summary>Writes a whole frame. Replies and unsolicited frames both go through here, so they cannot interleave.</summary>
        public async Task WriteAsync(ReadOnlyMemory<byte> frame)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Never cancelled: an interrupted write would leave half a frame on the wire. Closing the stream ends it.
                await Stream.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Ends the connection: cancels <see cref="Token"/> and closes the socket. Safe to call more than once and from any thread.</summary>
        public void Close()
        {
            try
            {
                _closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, so already closed.
            }

            Stream.Dispose();
        }

        public void Dispose()
        {
            Close();
            _closed.Dispose();
        }
    }
}
