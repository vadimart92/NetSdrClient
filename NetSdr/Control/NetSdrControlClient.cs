using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Control;

/// <summary>
/// Client of the NetSDR control channel. One request is in flight at a time because the protocol has no
/// transaction identifiers; concurrent calls queue in arrival order. Frames the device sends on its own
/// accord arrive on <see cref="Unsolicited"/>.
/// </summary>
public sealed class NetSdrControlClient : IAsyncDisposable
{
    private const int CodeSize = 2;
    private const int FramePrefixSize = FrameHeader.Size + CodeSize;
    private const int MaxPayloadSize = FrameHeader.MaxEncodableLength - FramePrefixSize;

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

    // Written under _sync. _state is also read without the lock by IsConnected.
    private volatile State _state;
    private PendingRequest? _pending;
    private Exception? _fault;
    private Stream? _output;
    private Task? _readLoop;

    /// <exception cref="ArgumentOutOfRangeException"><see cref="NetSdrControlClientOptions.UnsolicitedCapacity"/> is below 1.</exception>
    public NetSdrControlClient(NetSdrControlClientOptions? options = null)
    {
        options ??= new NetSdrControlClientOptions();
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

    /// <summary>Sets a control item and returns the item the device echoes back.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The item does not fit in one frame.</exception>
    public Task<T> SetAsync<T>(T item, CancellationToken ct = default) where T : struct, IControlItem<T>
    {
        var pending = new PendingRequest<T>(RequestType.Set);
        RentedFrame frame = RentFrame(RequestType.Set, T.Code, T.GetSize(in item));
        try
        {
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
        RequestAsync<T>(RequestType.Get, default, ct);

    /// <summary>Requests a control item identified by <paramref name="key"/>, sent as its raw little-endian bytes (for example a channel number).</summary>
    public Task<T> GetAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged =>
        RequestAsync<T>(RequestType.Get, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), ct);

    /// <summary>Requests the range of a control item; the device answers with a <c>RangeResponse</c>.</summary>
    public Task<T> GetRangeAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged =>
        RequestAsync<T>(RequestType.GetRange, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), ct);

    /// <summary>
    /// Sends a request for any item code and returns the device's reply uninterpreted. The reply type is
    /// <c>RangeResponse</c> for <see cref="RequestType.GetRange"/> and <c>Response</c> otherwise.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> is not <c>Set</c>, <c>Get</c> or <c>GetRange</c>, or the payload does not fit in one frame.
    /// </exception>
    public Task<ControlItemMessage> SendAsync(
        RequestType type, ushort code, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (type is not (RequestType.Set or RequestType.Get or RequestType.GetRange))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only Set, Get and GetRange requests can be sent.");
        }

        var pending = new PendingRawRequest(code, type);
        RentedFrame frame = RentFrame(type, code, payload.Length);
        payload.Span.CopyTo(frame.Payload);
        return ExchangeAsync(pending, pending.Reply, frame, ct);
    }

    /// <summary>Stops the client and closes the connection. Fails the request in flight with <see cref="ObjectDisposedException"/>.</summary>
    public ValueTask DisposeAsync()
    {
        PendingRequest? pending;
        Task? readLoop;
        lock (_sync)
        {
            if (_state == State.Disposed)
            {
                return new ValueTask(_disposed.Task);
            }

            _state = State.Disposed;
            pending = _pending;
            _pending = null;
            readLoop = _readLoop;
        }

        return DisposeCoreAsync(pending, readLoop);
    }

    /// <summary>Starts the read loop on an established connection.</summary>
    /// <exception cref="InvalidOperationException">The client is already attached or has faulted.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    internal void Attach(PipeReader input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        lock (_sync)
        {
            switch (_state)
            {
                case State.NotConnected:
                    break;
                case State.Disposed:
                    throw new ObjectDisposedException(nameof(NetSdrControlClient));
                default:
                    throw new InvalidOperationException("The client is already attached to a connection.");
            }

            _output = output;
            _state = State.Connected;
            _readLoop = Task.Run(() => ReadLoopAsync(input));
        }
    }

    private async ValueTask DisposeCoreAsync(PendingRequest? pending, Task? readLoop)
    {
        try
        {
            Finish(error: null);
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
        }
        finally
        {
            _disposed.TrySetResult();
        }
    }

    private Task<T> RequestAsync<T>(RequestType type, ReadOnlySpan<byte> payload, CancellationToken ct)
        where T : struct, IControlItem<T>
    {
        var pending = new PendingRequest<T>(type);
        RentedFrame frame = RentFrame(type, T.Code, payload.Length);
        payload.CopyTo(frame.Payload);
        return ExchangeAsync(pending, pending.Reply, frame, ct);
    }

    /// <summary>
    /// Waits for the request's turn, writes the frame and waits for the reply. Takes ownership of
    /// <paramref name="frame"/> and returns it to the pool when done.
    /// </summary>
    private async Task<TResult> ExchangeAsync<TResult>(
        PendingRequest pending, Task<TResult> reply, RentedFrame frame, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Stream output = Register(pending);
                try
                {
                    // Never cancelled: an interrupted write would leave half a frame on the wire.
                    await output.WriteAsync(frame.Memory, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Faulting fails the pending request, so awaiting the reply below throws the failure.
                    Fault(ex as IOException ?? new IOException("Failed to write a request to the device.", ex));
                }

                return await reply.ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            frame.Return();
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

    private PendingRequest? TakePending()
    {
        lock (_sync)
        {
            PendingRequest? pending = _pending;
            _pending = null;
            return pending;
        }
    }

    private static RentedFrame RentFrame(RequestType type, ushort code, int payloadSize)
    {
        if ((uint)payloadSize > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                "payload",
                payloadSize,
                $"A payload of {payloadSize} bytes does not fit in a control frame; the limit is {MaxPayloadSize} bytes.");
        }

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
        var replyType = (ReplyType)type;
        if (replyType > ReplyType.RangeResponse)
        {
            // Data items and acknowledgements have no item code; the payload is everything after the header.
            Publish(replyType, 0, frame.Slice(FrameHeader.Size));
            return;
        }

        if (length == FrameHeader.Size)
        {
            // A header-only frame is a NAK. Only a Response can answer the request in flight.
            if (replyType == ReplyType.Response && TakePending() is { } rejected)
            {
                rejected.Fail(new NetSdrNakException(rejected.Code, rejected.RequestType));
            }
            else
            {
                Publish(replyType, 0, ReadOnlySequence<byte>.Empty);
            }

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
            Publish(replyType, code, payload);
        }
        else
        {
            HandleReply(replyType, code, payload);
        }
    }

    private void HandleReply(ReplyType type, ushort code, ReadOnlySequence<byte> payload)
    {
        PendingRequest? pending = TakePending();
        if (pending is null)
        {
            Publish(type, code, payload);
            return;
        }

        if (pending.Code == code && pending.ExpectedType == type)
        {
            CompletePending(pending, payload);
            return;
        }

        // The foreign frame is readable from Unsolicited before the caller learns its request failed.
        Publish(type, code, payload);
        pending.Fail(new NetSdrProtocolException(
            $"Expected {pending.ExpectedType} for item 0x{pending.Code:X4} but received {type} for item 0x{code:X4}."));
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

    private void Publish(ReplyType type, ushort code, ReadOnlySequence<byte> payload)
    {
        ReadOnlyMemory<byte> copy = payload.IsEmpty ? ReadOnlyMemory<byte>.Empty : payload.ToArray();
        _unsolicited.Writer.TryWrite(new ControlItemMessage(type, code, copy));
    }

    /// <summary>Moves a connected client to the faulted state. Does nothing if it already faulted or was disposed.</summary>
    private void Fault(Exception exception)
    {
        PendingRequest? pending;
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
        _unsolicited.Writer.TryComplete();
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else if (_completion.TrySetException(error))
        {
            // Reading Exception marks it observed, so a client nobody awaits does not raise an unobserved task exception.
            _ = _completion.Task.Exception;
        }

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
