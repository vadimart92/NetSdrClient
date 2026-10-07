using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Control;

/// <summary>
/// Client of the NetSDR control channel. One request is in flight at a time because the protocol has no
/// transaction identifiers; concurrent calls queue in arrival order. Frames the device sends on its own
/// accord arrive on <see cref="Unsolicited"/>.
/// </summary>
public sealed class NetSdrControlClient : INetSdrControlClient
{
    private const int CodeSize = 2;
    private const int FramePrefixSize = FrameHeader.Size + CodeSize;

    /// <summary>The largest payload a control frame carries after its header and item code.</summary>
    internal const int MaxPayloadSize = FrameHeader.MaxEncodableLength - FramePrefixSize;

    private enum State
    {
        NotConnected,
        Connected,
        Faulted,
        Disposed,
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Channel<ControlItemMessage> _unsolicited;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _responseTimeout;
    private readonly bool _faultOnTimeout;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _supervised;

    // Written under _sync. _state is also read without the lock by IsConnected.
    private volatile State _state;
    private PendingRequest? _pending;
    // The item and reply type of the one request the device may still answer after its caller stopped waiting:
    // it was cancelled after it was sent, it timed out without faulting the client, or a reply for another item or
    // of the wrong type failed it. A reply with this pair that does not match the request in flight is taken for that
    // late reply and goes to Unsolicited. A reply that does match the request in flight answers it and ends the wait.
    private (ushort Code, ReplyType Type)? _abandoned;
    private Exception? _fault;
    private Stream? _output;
    private Task? _readLoop;
    private IPEndPoint? _localEndPoint;
    private IPEndPoint? _remoteEndPoint;

    /// <exception cref="ArgumentNullException"><see cref="NetSdrControlClientOptions.LoggerFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="NetSdrControlClientOptions.UnsolicitedCapacity"/> is below 1, or
    /// <see cref="NetSdrControlClientOptions.ResponseTimeout"/> is neither positive (up to <see cref="int.MaxValue"/> milliseconds)
    /// nor <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public NetSdrControlClient(NetSdrControlClientOptions? options = null)
    {
        options ??= new NetSdrControlClientOptions();
        ArgumentNullException.ThrowIfNull(options.LoggerFactory, nameof(options));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options));
        TimeSpan timeout = options.ResponseTimeout;
        if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                timeout,
                "ResponseTimeout must be positive (at most Int32.MaxValue milliseconds) or Timeout.InfiniteTimeSpan.");
        }

        _responseTimeout = timeout;
        _faultOnTimeout = options.FaultOnTimeout;
        _logger = options.LoggerFactory.CreateLogger(typeof(NetSdrControlClient).FullName!);
        _timeProvider = options.TimeProvider;
        _supervised = options.Supervised;
        _unsolicited = Channel.CreateBounded<ControlItemMessage>(new BoundedChannelOptions(options.UnsolicitedCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    /// <summary>
    /// Everything the device sends other than the reply to the request in flight: <c>Unsolicited</c> frames,
    /// data items, acknowledgements, and responses nobody is waiting for. Completes without error when the client
    /// is disposed or faulted.
    /// </summary>
    public ChannelReader<ControlItemMessage> Unsolicited => _unsolicited.Reader;

    /// <summary>
    /// Completes successfully when the client is disposed, and with the cause when it faults
    /// (a protocol violation or the device closing the connection).
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>Whether the client is attached to a connection and has not faulted or been disposed.</summary>
    public bool IsConnected => _state == State.Connected;

    /// <summary>
    /// Called on the read loop, outside the client's lock and before the message goes to <see cref="Unsolicited"/>, when
    /// the device answers a request nobody waits for any more: with <see langword="false"/> for its late reply, and with
    /// <see langword="true"/> for a <c>Response</c> NAK that arrives with no request in flight while such a reply is still
    /// expected. The message is the one <see cref="Unsolicited"/> receives; a NAK is <c>(Response, 0, empty)</c>.
    /// Set before the client connects. It must neither block nor throw: an exception faults the client.
    /// </summary>
    internal Action<ControlItemMessage, bool>? LateReplyObserver { get; set; }

    /// <summary>
    /// The local end of the TCP connection, set by <c>ConnectAsync</c>; <see langword="null"/> before the client has
    /// connected and for a client attached to something other than a socket. The address is the one the device sees
    /// the client at, so it is what to give the device for a data stream, together with the port of the data receiver:
    /// <c>new IPEndPoint(client.LocalEndPoint.Address, receiver.LocalEndPoint.Port)</c> passed to
    /// <see cref="DataOutputUdpAddress.For"/>. It stays available after the client is disconnected or disposed.
    /// </summary>
    public IPEndPoint? LocalEndPoint
    {
        get
        {
            lock (_sync)
            {
                return _localEndPoint;
            }
        }
    }

    /// <summary>
    /// The device's end of the TCP connection, set by <c>ConnectAsync</c>; <see langword="null"/> before the client has
    /// connected and for a client attached to something other than a socket. It stays available after the client is
    /// disconnected or disposed.
    /// </summary>
    public IPEndPoint? RemoteEndPoint
    {
        get
        {
            lock (_sync)
            {
                return _remoteEndPoint;
            }
        }
    }

    /// <summary>Connects to the device over TCP and starts the client.</summary>
    /// <param name="host">Host name or IP address of the device.</param>
    /// <param name="port">TCP port of the control channel; the device listens on 50000 by default.</param>
    /// <exception cref="InvalidOperationException">The client is already connected or has faulted.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public async Task ConnectAsync(string host, int port = 50000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ThrowIfCannotAttach();

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        AttachSocket(socket);
    }

    /// <summary>Connects to the device over TCP and starts the client.</summary>
    /// <exception cref="InvalidOperationException">The client is already connected or has faulted.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public async Task ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ThrowIfCannotAttach();

        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        AttachSocket(socket);
    }

    /// <summary>Sets a control item and returns the item the device echoes back.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The item does not fit in one frame.</exception>
    public Task<T> SetAsync<T>(T item, CancellationToken ct = default) where T : struct, IControlItem<T>
    {
        var pending = new PendingRequest<T>(RequestType.Set);
        RentedFrame frame = RentFrame(RequestType.Set, T.Code, T.GetSize(in item), nameof(item));
        try
        {
            // The buffer comes from a pool and may hold an earlier frame: an item that writes fewer bytes than
            // its GetSize promised must send zeros, not another request's leftovers.
            frame.Payload.Clear();
            T.Write(in item, frame.Payload);
        }
        catch
        {
            frame.Return();
            throw;
        }

        return ExchangeAsync(pending, pending.Reply, frame, ct);
    }

    /// <summary>Requests a control item that needs no key.</summary>
    public Task<T> GetAsync<T>(CancellationToken ct = default) where T : struct, IControlItem<T> =>
        RequestAsync<T>(RequestType.Get, default, null, ct);

    /// <summary>Requests a control item identified by <paramref name="key"/>, sent as its raw little-endian bytes (for example a channel number).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key does not fit in one frame.</exception>
    public Task<T> GetAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged =>
        RequestAsync<T>(RequestType.Get, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), nameof(key), ct);

    /// <summary>Requests the range of a control item; the device answers with a <c>RangeResponse</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key does not fit in one frame.</exception>
    public Task<T> GetRangeAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged =>
        RequestAsync<T>(RequestType.GetRange, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), nameof(key), ct);

    /// <summary>
    /// Sends a request for any item code and returns the device's reply uninterpreted. The reply type is
    /// <c>RangeResponse</c> for <see cref="RequestType.GetRange"/> and <c>Response</c> otherwise.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> is not <c>Set</c>, <c>Get</c> or <c>GetRange</c>, or the payload does not fit in one frame.
    /// </exception>
    public Task<ControlItemMessage> SendAsync(
        RequestType type, ushort code, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
        SendAsync(type, code, payload, item: null, ct);

    /// <summary>The raw request path of the public <c>SendAsync</c>, with the item name the logs show.</summary>
    /// <param name="item">The item name in the logs; <see langword="null"/> logs it as <c>raw</c>.</param>
    internal Task<ControlItemMessage> SendAsync(
        RequestType type, ushort code, ReadOnlyMemory<byte> payload, string? item, CancellationToken ct)
    {
        if (type is not (RequestType.Set or RequestType.Get or RequestType.GetRange))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only Set, Get and GetRange requests can be sent.");
        }

        var pending = new PendingRawRequest(code, type, item);
        RentedFrame frame = RentFrame(type, code, payload.Length, nameof(payload));
        payload.Span.CopyTo(frame.Payload);
        return ExchangeAsync(pending, pending.Reply, frame, ct);
    }

    /// <summary>
    /// Stops the client and closes the connection. Fails the request in flight with <see cref="ObjectDisposedException"/>.
    /// A client that had already faulted keeps its fault in <see cref="Completion"/>.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        PendingRequest? pending;
        Task? readLoop;
        Exception? fault;
        bool wasConnected;
        IPEndPoint? remote;
        lock (_sync)
        {
            if (_state == State.Disposed)
            {
                return new ValueTask(_disposed.Task);
            }

            wasConnected = _state == State.Connected;
            remote = _remoteEndPoint;
            fault = _state == State.Faulted ? _fault : null;
            _state = State.Disposed;
            pending = _pending;
            _pending = null;
            readLoop = _readLoop;
        }

        return DisposeCoreAsync(fault, pending, readLoop, wasConnected, remote);
    }

    /// <summary>Starts the read loop on an established connection.</summary>
    /// <exception cref="InvalidOperationException">The client is already attached or has faulted.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    internal void Attach(PipeReader input, Stream output) => Attach(input, output, null, null);

    private void Attach(PipeReader input, Stream output, IPEndPoint? localEndPoint, IPEndPoint? remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        lock (_sync)
        {
            ThrowIfCannotAttach();
            _output = output;
            _localEndPoint = localEndPoint;
            _remoteEndPoint = remoteEndPoint;
            _state = State.Connected;
            _readLoop = Task.Run(() => ReadLoopAsync(input));
        }

        try
        {
            ControlClientLog.Connected(
                _logger, _supervised ? LogLevel.Debug : LogLevel.Information, remoteEndPoint, localEndPoint);
        }
        catch (Exception)
        {
            // A logging provider failed. The client is attached and reading: throwing now would make the caller take
            // a running client for a failed connect, and AttachSocket would close the stream under it.
        }
    }

    /// <summary>Attaches an established connection. Closes it if the client cannot take it.</summary>
    private void AttachSocket(Socket socket)
    {
        // Read before the stream takes the socket: they are gone once it is closed.
        IPEndPoint? local = AsIPv4IfMapped(socket.LocalEndPoint as IPEndPoint);
        IPEndPoint? remote = AsIPv4IfMapped(socket.RemoteEndPoint as IPEndPoint);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            Attach(PipeReader.Create(stream), stream, local, remote);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The socket that <c>ConnectAsync(string, int)</c> creates is dual-mode, and reports the IPv4 address it is connected
    /// over as an IPv4-mapped IPv6 address, which <see cref="DataOutputUdpAddress.For"/> does not accept.
    /// </summary>
    private static IPEndPoint? AsIPv4IfMapped(IPEndPoint? endPoint) =>
        endPoint is { Address.IsIPv4MappedToIPv6: true }
            ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port)
            : endPoint;

    /// <exception cref="InvalidOperationException">The client is already attached or has faulted.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    private void ThrowIfCannotAttach()
    {
        lock (_sync)
        {
            switch (_state)
            {
                case State.NotConnected:
                    return;
                case State.Disposed:
                    throw new ObjectDisposedException(nameof(NetSdrControlClient));
                case State.Faulted:
                    throw new InvalidOperationException("The client has faulted; create a new client.", _fault);
                default:
                    throw new InvalidOperationException("The client is already attached to a connection.");
            }
        }
    }

    /// <param name="wasConnected">The client was connected and had not faulted, so the closure is logged.</param>
    /// <param name="remote">The device's end of the connection, for the log.</param>
    private async ValueTask DisposeCoreAsync(
        Exception? fault, PendingRequest? pending, Task? readLoop, bool wasConnected, IPEndPoint? remote)
    {
        try
        {
            // A client that faulted before the disposal keeps its fault, whichever of the two finishes first.
            Finish(fault);
        }
        finally
        {
            pending?.Fail(new ObjectDisposedException(nameof(NetSdrControlClient)));
        }

        try
        {
            if (readLoop is not null)
            {
                await readLoop.ConfigureAwait(false);
            }

            if (wasConnected)
            {
                ControlClientLog.Closed(_logger, _supervised ? LogLevel.Debug : LogLevel.Information, remote);
            }
        }
        finally
        {
            _disposed.TrySetResult();
        }
    }

    /// <param name="payloadName">The public parameter the payload comes from, for the size error; <see langword="null"/> when there is none.</param>
    private Task<T> RequestAsync<T>(RequestType type, ReadOnlySpan<byte> payload, string? payloadName, CancellationToken ct)
        where T : struct, IControlItem<T>
    {
        var pending = new PendingRequest<T>(type);
        RentedFrame frame = RentFrame(type, T.Code, payload.Length, payloadName);
        payload.CopyTo(frame.Payload);
        return ExchangeAsync(pending, pending.Reply, frame, ct);
    }

    /// <summary>
    /// Waits for the request's turn, writes the frame and waits for the reply. Takes ownership of
    /// <paramref name="frame"/> and returns it to the pool as soon as it has been written.
    /// </summary>
    private async Task<TResult> ExchangeAsync<TResult>(
        PendingRequest pending, Task<TResult> reply, RentedFrame frame, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            frame.Return();
            throw;
        }

        try
        {
            await SendFrameAsync(pending, frame, ct).ConfigureAwait(false);
            return await AwaitReplyAsync(pending, reply, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes <paramref name="pending"/> the request in flight and writes <paramref name="frame"/>, which it then returns to the pool.
    /// A write failure faults the client, which fails the pending request.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled before anything was written.</exception>
    private async Task SendFrameAsync(PendingRequest pending, RentedFrame frame, CancellationToken ct)
    {
        try
        {
            // The token may have been cancelled just as the request's turn came.
            ct.ThrowIfCancellationRequested();
            Stream output = Register(pending);
            try
            {
                // Never cancelled: an interrupted write would leave half a frame on the wire.
                await output.WriteAsync(frame.Memory, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fault(ex as IOException ?? new IOException("Failed to write a request to the device.", ex));
                return;
            }

            try
            {
                ControlClientLog.RequestSent(_logger, pending.RequestType, pending.Item, pending.Code, frame.Payload.Length);
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    ControlClientLog.FrameSent(_logger, Convert.ToHexString(frame.Memory.Span));
                }
            }
            catch (Exception)
            {
                // A logging provider failed. The request is on the wire and stays the request in flight, so its caller
                // still waits for the reply: leaving now would free the gate while the reply can still arrive.
            }
        }
        finally
        {
            frame.Return();
        }
    }

    /// <summary>
    /// Waits for the reply to the request in flight. On a timeout or cancellation the request is dropped:
    /// the client faults if it was a timeout and <see cref="NetSdrControlClientOptions.FaultOnTimeout"/> is set,
    /// otherwise the request is remembered as abandoned so that its late reply is not taken for the answer to a later
    /// request for a different item. A later request for the same item can still be answered by the late reply; see
    /// <see cref="NetSdrControlClientOptions.FaultOnTimeout"/>.
    /// </summary>
    private async Task<TResult> AwaitReplyAsync<TResult>(PendingRequest pending, Task<TResult> reply, CancellationToken ct)
    {
        try
        {
            return await reply.WaitAsync(_responseTimeout, _timeProvider, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            bool timedOut = ex is TimeoutException;
            bool faultClient = timedOut && _faultOnTimeout;
            if (!TryAbandon(pending, remember: !faultClient))
            {
                // The reader, a fault or the disposal took the request at the same moment and completes it
                // right away, so the outcome is theirs, not the timeout's.
                return await reply.ConfigureAwait(false);
            }

            if (!timedOut)
            {
                try
                {
                    ControlClientLog.RequestAbandoned(_logger, pending.RequestType, pending.Item, pending.Code);
                }
                catch (Exception)
                {
                    // A logging provider failed; the caller still learns about its own cancellation.
                }

                throw;
            }

            var timeout = new TimeoutException(
                $"The device did not reply to the {pending.RequestType} request for item 0x{pending.Code:X4} " +
                $"within {_responseTimeout.TotalMilliseconds:0} ms.");
            try
            {
                ControlClientLog.RequestTimedOut(
                    _logger, _supervised ? LogLevel.Debug : LogLevel.Warning,
                    pending.RequestType, pending.Item, pending.Code, _responseTimeout, faultClient);
            }
            catch (Exception)
            {
                // A logging provider failed; the caller still learns about the timeout.
            }

            if (faultClient)
            {
                // The terminal state is complete before the caller learns about the timeout.
                Fault(timeout);
            }

            throw timeout;
        }
    }

    /// <summary>
    /// Withdraws <paramref name="pending"/> as the request in flight. Returns <see langword="false"/> when something
    /// else has already taken it, which then owns its outcome.
    /// </summary>
    /// <param name="remember">Record the request as abandoned.</param>
    private bool TryAbandon(PendingRequest pending, bool remember)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, pending))
            {
                return false;
            }

            _pending = null;
            if (remember)
            {
                _abandoned = (pending.Code, pending.ExpectedType);
            }

            return true;
        }
    }

    /// <summary>Makes <paramref name="pending"/> the request in flight and returns the stream to write it to.</summary>
    private Stream Register(PendingRequest pending)
    {
        lock (_sync)
        {
            switch (_state)
            {
                case State.Connected:
                    // Before the write and under the lock, so the reader loop always sees it when the reply arrives.
                    pending.WriteStartedAt = _timeProvider.GetTimestamp();
                    _pending = pending;
                    return _output!;
                case State.NotConnected:
                    throw new InvalidOperationException("The client is not connected.");
                case State.Faulted:
                    throw new InvalidOperationException("The client has faulted; create a new client.", _fault);
                default:
                    throw new ObjectDisposedException(nameof(NetSdrControlClient));
            }
        }
    }

    /// <summary>Rejects a payload that does not fit in one control frame.</summary>
    /// <param name="paramName">The public parameter the payload comes from, named in the error; <see langword="null"/> when there is none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadSize"/> is negative or above <see cref="MaxPayloadSize"/>.</exception>
    internal static void ThrowIfPayloadTooLarge(int payloadSize, string? paramName)
    {
        if ((uint)payloadSize > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                payloadSize,
                $"A payload of {payloadSize} bytes does not fit in a control frame; the limit is {MaxPayloadSize} bytes.");
        }
    }

    /// <param name="paramName">The public parameter the payload comes from, named in the error when it is too large.</param>
    private static RentedFrame RentFrame(RequestType type, ushort code, int payloadSize, string? paramName)
    {
        ThrowIfPayloadTooLarge(payloadSize, paramName);
        int length = FramePrefixSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        FrameHeader.Write(buffer, length, (byte)type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(FrameHeader.Size), code);
        return new RentedFrame(buffer, length);
    }

    private async Task ReadLoopAsync(PipeReader input)
    {
        try
        {
            while (true)
            {
                ReadResult result = await input.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                while (TryTakeFrame(ref buffer, out ReadOnlySequence<byte> frame, out byte type, out int length))
                {
                    ProcessFrame(frame, type, length);
                }

                // Consumed up to the first incomplete frame; everything buffered has been examined, so the
                // next read waits for more bytes.
                input.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    throw new IOException("Connection closed by the device.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The client was disposed or has faulted, and has already shut itself down.
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            try
            {
                input.Complete();
            }
            catch (Exception)
            {
                // The client is shutting down; a failure to release the reader has nobody to report to.
            }
        }
    }

    /// <summary>Splits the next whole frame off <paramref name="buffer"/>; false when the buffer holds only part of one.</summary>
    /// <exception cref="NetSdrProtocolException">The header is invalid.</exception>
    private static bool TryTakeFrame(
        ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame, out byte type, out int length)
    {
        frame = default;
        type = 0;
        length = 0;
        if (buffer.Length < FrameHeader.Size)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[FrameHeader.Size];
        buffer.Slice(0, FrameHeader.Size).CopyTo(header);
        if (!FrameHeader.TryRead(header, out length, out type))
        {
            throw new NetSdrProtocolException(
                $"Invalid frame header 0x{BinaryPrimitives.ReadUInt16LittleEndian(header):X4}: the length field is below {FrameHeader.Size}.");
        }

        if (buffer.Length < length)
        {
            return false;
        }

        frame = buffer.Slice(0, length);
        buffer = buffer.Slice(length);
        return true;
    }

    /// <exception cref="NetSdrProtocolException">The frame is malformed.</exception>
    private void ProcessFrame(ReadOnlySequence<byte> frame, byte type, int length)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            ControlClientLog.FrameReceived(
                _logger, Convert.ToHexString(frame.IsSingleSegment ? frame.FirstSpan : frame.ToArray()));
        }

        var replyType = (ReplyType)type;
        if (replyType > ReplyType.RangeResponse)
        {
            // Data items and acknowledgements have no item code; the payload is everything after the header.
            Publish(replyType, 0, frame.Slice(FrameHeader.Size), PublishReason.Data);
            return;
        }

        if (length == FrameHeader.Size)
        {
            HandleNak(replyType);
            return;
        }

        if (length < FramePrefixSize)
        {
            throw new NetSdrProtocolException(
                $"A {replyType} frame of {length} bytes is truncated: it must carry a {CodeSize}-byte item code.");
        }

        Span<byte> codeBytes = stackalloc byte[CodeSize];
        frame.Slice(FrameHeader.Size, CodeSize).CopyTo(codeBytes);
        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(codeBytes);
        ReadOnlySequence<byte> payload = frame.Slice(FramePrefixSize);

        if (replyType == ReplyType.Unsolicited)
        {
            Publish(replyType, code, payload, PublishReason.Unsolicited);
        }
        else
        {
            HandleReply(replyType, code, payload);
        }
    }

    /// <summary>
    /// A header-only frame is a NAK. Only a <c>Response</c> NAK answers the request in flight. A header-only frame of
    /// another type, even while a request is in flight, and a <c>Response</c> NAK with no request in flight answer
    /// nothing and go to <see cref="Unsolicited"/>; the latter also ends the wait for the reply of an abandoned
    /// request, which <see cref="LateReplyObserver"/> sees.
    /// </summary>
    private void HandleNak(ReplyType type)
    {
        PendingRequest? rejected = null;
        bool late = false;
        if (type == ReplyType.Response)
        {
            lock (_sync)
            {
                rejected = _pending;
                _pending = null;
                if (rejected is null)
                {
                    // Read before it is cleared: the NAK may be the device's answer to the abandoned request.
                    late = _abandoned is not null;
                    _abandoned = null;
                }
            }
        }

        if (rejected is null)
        {
            var message = new ControlItemMessage(type, 0, ReadOnlyMemory<byte>.Empty);
            if (late)
            {
                LateReplyObserver?.Invoke(message, true);
            }

            Publish(message, PublishReason.Nak);
            return;
        }

        try
        {
            ControlClientLog.NakReceived(
                _logger, rejected.RequestType, rejected.Item, rejected.Code,
                _timeProvider.GetElapsedTime(rejected.WriteStartedAt));
        }
        finally
        {
            rejected.Fail(new NetSdrNakException(rejected.Code, rejected.RequestType));
        }
    }

    private void HandleReply(ReplyType type, ushort code, ReadOnlySequence<byte> payload)
    {
        PendingRequest? pending = null;
        bool answers = false;
        bool late = false;
        lock (_sync)
        {
            PendingRequest? active = _pending;
            if (active is not null && active.Code == code && active.ExpectedType == type)
            {
                // The reply answers the request in flight, even when the abandoned slot holds the same pair:
                // the device is evidently answering that item again, so the wait for the old reply ends here.
                _pending = null;
                pending = active;
                answers = true;
                if (_abandoned is { } same && same.Code == code && same.Type == type)
                {
                    _abandoned = null;
                }
            }
            else if (_abandoned is { } abandoned && abandoned.Code == code && abandoned.Type == type)
            {
                // The late reply of a request nobody waits for. It is consumed, and the request in flight stays untouched.
                _abandoned = null;
                late = true;
            }
            else if (active is not null)
            {
                // Neither reply is expected. The device's real reply to the request that is about to fail may still follow.
                _pending = null;
                pending = active;
                _abandoned = (active.Code, active.ExpectedType);
            }
        }

        if (pending is null)
        {
            ControlItemMessage message = ToMessage(type, code, payload);
            if (late)
            {
                LateReplyObserver?.Invoke(message, false);
            }

            Publish(message, late ? PublishReason.LateReply : PublishReason.NoRequest);
            return;
        }

        if (answers)
        {
            try
            {
                ControlClientLog.ReplyReceived(
                    _logger, type, pending.Item, code, _timeProvider.GetElapsedTime(pending.WriteStartedAt),
                    (int)payload.Length);
            }
            finally
            {
                CompletePending(pending, payload);
            }

            return;
        }

        try
        {
            ControlClientLog.ForeignReply(_logger, pending.ExpectedType, pending.Code, type, code);

            // The foreign frame is readable from Unsolicited before the caller learns its request failed.
            Publish(type, code, payload, PublishReason.Foreign);
        }
        finally
        {
            pending.Fail(new NetSdrProtocolException(
                $"Expected {pending.ExpectedType} for item 0x{pending.Code:X4} but received {type} for item 0x{code:X4}."));
        }
    }

    /// <summary>Reads the reply straight from the pipe buffer, copying only when the payload spans several segments.</summary>
    private static void CompletePending(PendingRequest pending, ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            pending.Complete(payload.FirstSpan);
            return;
        }

        int length = (int)payload.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            payload.CopyTo(rented);
            pending.Complete(rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Publish(ReplyType type, ushort code, ReadOnlySequence<byte> payload, PublishReason reason) =>
        Publish(ToMessage(type, code, payload), reason);

    private void Publish(ControlItemMessage message, PublishReason reason)
    {
        ControlClientLog.MessagePublished(_logger, message.Type, message.Code, message.Payload.Length, reason);
        _unsolicited.Writer.TryWrite(message);
    }

    /// <summary>Copies the payload out of the pipe buffer, which is reused once the frame is consumed.</summary>
    private static ControlItemMessage ToMessage(ReplyType type, ushort code, ReadOnlySequence<byte> payload) =>
        new(type, code, payload.IsEmpty ? ReadOnlyMemory<byte>.Empty : payload.ToArray());

    /// <summary>Moves a connected client to the faulted state. Does nothing if it already faulted or was disposed.</summary>
    private void Fault(Exception exception)
    {
        PendingRequest? pending;
        IPEndPoint? remote;
        lock (_sync)
        {
            if (_state != State.Connected)
            {
                return;
            }

            _state = State.Faulted;
            _fault = exception;
            pending = _pending;
            _pending = null;
            remote = _remoteEndPoint;
        }

        // Logged first, so whoever sees Completion fail finds the event already written.
        try
        {
            ControlClientLog.Faulted(_logger, _supervised ? LogLevel.Debug : LogLevel.Error, remote, exception);
        }
        catch (Exception)
        {
            // A logging provider failed. The fault still completes, and the callers of Fault (a failed write, a
            // timeout, the read loop) still report the fault itself rather than the provider's exception.
        }

        // The terminal state is complete before the caller learns its request failed.
        try
        {
            Finish(exception);
        }
        finally
        {
            pending?.Fail(exception);
        }
    }

    /// <summary>Ends the client's outputs and closes the connection. Safe to call more than once.</summary>
    /// <param name="error">The cause of a fault, or <see langword="null"/> for a normal disposal.</param>
    private void Finish(Exception? error)
    {
        // Completion first: whoever sees Unsolicited end can rely on Completion already holding its outcome.
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else if (_completion.TrySetException(error))
        {
            // Reading Exception marks it observed, so a client nobody awaits does not raise an unobserved task exception.
            _ = _completion.Task.Exception;
        }

        _unsolicited.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            _output?.Dispose();
        }
        catch (Exception)
        {
            // The connection is being dropped; a failure to close it has nobody to report to.
        }
    }

    /// <summary>A request frame in a pooled buffer.</summary>
    private readonly struct RentedFrame
    {
        private readonly byte[] _buffer;
        private readonly int _length;

        public RentedFrame(byte[] buffer, int length)
        {
            _buffer = buffer;
            _length = length;
        }

        public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

        public Span<byte> Payload => _buffer.AsSpan(FramePrefixSize, _length - FramePrefixSize);

        public void Return() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
