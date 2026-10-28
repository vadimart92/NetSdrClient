using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NetSdr.Data;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Testing;

// The data channel: UDP datagrams of Data0 frames, started by hand or by the receiver state the client sets.
public sealed partial class NetSdrTestServer
{
    private const int SequenceSize = 2;
    private const int DatagramPrefixSize = FrameHeader.Size + SequenceSize;

    // The largest payload of an IPv4 UDP datagram.
    private const int MaxDatagramSize = 65507;
    private const int MaxPayloadSize = MaxDatagramSize - DatagramPrefixSize;

    private const int DataTypeShift = 13;
    private const double DefaultSampleRate = 200_000;

    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private volatile bool _autoStream = true;

    // Guarded by _streamGate, which is held while a stream starts or stops, never while the server calls user code.
    private StreamRun? _stream;

    // The first exception a streaming loop ended with (a failing Source or DropPacket); DisposeAsync reports it.
    private Exception? _streamFault;

    /// <summary>
    /// The settings of a stream when none are given: for <see cref="StartStreamingAsync"/> and for the streams the server
    /// starts itself, see <see cref="AutoStream"/>. Read when a stream starts.
    /// </summary>
    public StreamOptions Stream { get; } = new();

    /// <summary>
    /// When true (the default) and no handler is set for the receiver state (0x0018), a Set of that item starts or stops a
    /// stream: Run answers first and then starts it, Stop stops it first and then answers. The stream follows
    /// <see cref="Stream"/>, and what that leaves open comes from the device state: the format from the capture mode, the
    /// packet size from 0x00C4, the sample rate from 0x00B8 and the target from 0x00C5 (the client's address when that
    /// has none, or its address with the 0x00C5 port when the address is 0.0.0.0). A Run the server cannot honour, such as
    /// one aimed at a non-loopback address or one after a setting too short to read, is answered with a NAK. The stream ends when the client leaves. A handler of
    /// another item can run the same Set through <see cref="ControlReply.Then"/>.
    /// </summary>
    public bool AutoStream
    {
        get => _autoStream;
        set => _autoStream = value;
    }

    /// <summary>
    /// Starts sending data to <paramref name="target"/> without any command, whatever the device state says, and stops the
    /// stream that runs. The server sends from a loopback address, so the target must be an IPv4 loopback endpoint. The
    /// stream runs until <see cref="StopStreamingAsync"/> or <see cref="DisposeAsync"/>; the client leaving does not end it.
    /// </summary>
    /// <param name="target">Where to send the datagrams.</param>
    /// <param name="options">The settings; <see cref="Stream"/> when omitted.</param>
    /// <exception cref="ArgumentException">
    /// The target is not an IPv4 loopback endpoint with a port, or the options hold an unusable value: a format other
    /// than 16 or 24 bit, a payload that cannot hold one sample (and the timestamp, when on) or does not fit a datagram,
    /// an empty <see cref="StreamOptions.Packets"/> or an entry of it that is not whole samples or does not fit, a sample
    /// rate that is not positive, fewer than one channel. A running stream is left alone then.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    public async Task StartStreamingAsync(IPEndPoint target, StreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= Stream;
        SampleFormat format = options.Format ?? SampleFormat.Int16;
        StreamPlan plan = StreamPlan.Create(
            target,
            options,
            format,
            options.PayloadSize ?? DefaultPayloadSize(format, small: false),
            options.SampleRate ?? DefaultSampleRate);
        await StartStreamAsync(plan, owner: null).ConfigureAwait(false);
    }

    /// <summary>Stops the stream and returns when it has ended; does nothing when none runs.</summary>
    public async Task StopStreamingAsync()
    {
        await _streamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndStreamAsync().ConfigureAwait(false);
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private static int DefaultPayloadSize(SampleFormat format, bool small) => format == SampleFormat.Int24
        ? (small ? 384 : 1440)
        : (small ? 512 : 1024);

    /// <summary>Whether a request is a Set of the receiver state that <see cref="AutoStream"/> acts on.</summary>
    private bool IsAutoStreamRequest(ControlRequest request) =>
        AutoStream
        && request.Type == RequestType.Set
        && request.Code == ReceiverState.Code
        && request.Payload.Length >= Unsafe.SizeOf<ReceiverState>();

    /// <summary>
    /// Handles a Set of the receiver state for <see cref="AutoStream"/>. A Run is stored and answered, and the stream
    /// starts after the answer has been written; a Stop ends the stream and then is answered. A Run the server cannot
    /// honour throws, so the caller answers it with a NAK and nothing is stored.
    /// </summary>
    private async ValueTask<Dispatched> ReplyToReceiverStateAsync(ControlRequest request, Connection connection)
    {
        ReceiverState state = MemoryMarshal.Read<ReceiverState>(request.Payload.Span);
        if (state.Run == ReceiverState.Running)
        {
            StreamPlan plan = PlanFromState(state, connection);
            StoreState(request);
            return new Dispatched(ControlReply.Echo, () => StartStreamAsync(plan, connection));
        }

        StoreState(request);
        if (state.Run == ReceiverState.Idle)
        {
            await StopStreamingAsync().ConfigureAwait(false);
        }

        return new Dispatched(ControlReply.Echo);
    }

    /// <summary>Settles the stream a Run asks for: <see cref="Stream"/> first, then what the device state says.</summary>
    private StreamPlan PlanFromState(ReceiverState state, Connection connection)
    {
        StreamOptions options = Stream;
        SampleFormat format = options.Format ?? (state.Is24Bit ? SampleFormat.Int24 : SampleFormat.Int16);
        bool small = TryReadLatest(out DataOutputPacketSize size) && size.Size == DataOutputPacketSize.Small;
        double sampleRate = options.SampleRate ?? (TryReadLatest(out OutputSampleRate rate) && rate.Hz > 0 ? rate.Hz : DefaultSampleRate);

        IPEndPoint client = connection.RemoteEndPoint ?? throw new InvalidOperationException("The client's address is unknown.");
        IPEndPoint target = client;
        if (TryReadLatest(out DataOutputUdpAddress address))
        {
            target = new IPEndPoint(address.Ip == 0 ? client.Address : address.ToEndPoint().Address, address.Port);
        }

        return StreamPlan.Create(target, options, format, options.PayloadSize ?? DefaultPayloadSize(format, small), sampleRate);
    }

    /// <summary>Stops the running stream, if any, and starts the one described by <paramref name="plan"/>.</summary>
    /// <param name="plan">The stream to start.</param>
    /// <param name="owner">The client whose command started it, or <see langword="null"/> when started by hand.</param>
    private async Task StartStreamAsync(StreamPlan plan, Connection? owner)
    {
        await _streamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposal is not null, this);
            }

            await EndStreamAsync().ConfigureAwait(false);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            DataStream stream;
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                stream = new DataStream(plan, socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            var cancel = new CancellationTokenSource();
            var run = new StreamRun(owner, cancel);
            run.Completion = Task.Run(() => RunStreamAsync(stream, cancel.Token));
            _stream = run;
        }
        finally
        {
            _streamGate.Release();
        }
    }

    /// <summary>Ends the stream that <paramref name="client"/> started, if that is the one running. Called when the client leaves.</summary>
    private async Task EndStreamOfAsync(Connection client)
    {
        await _streamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_stream?.Owner, client))
            {
                await EndStreamAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _streamGate.Release();
        }
    }

    /// <summary>Cancels the running stream and waits for its loop to end. Call with <see cref="_streamGate"/> held.</summary>
    private async Task EndStreamAsync()
    {
        StreamRun? run = _stream;
        if (run is null)
        {
            return;
        }

        _stream = null;
        await run.Cancel.CancelAsync().ConfigureAwait(false);
        await run.Completion.ConfigureAwait(false);
        run.Cancel.Dispose();
    }

    /// <summary>The loop of one stream. It never faults: a failure is kept for <see cref="DisposeAsync"/>.</summary>
    private async Task RunStreamAsync(DataStream stream, CancellationToken token)
    {
        try
        {
            using (stream)
            {
                await stream.RunAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _streamFault, ex, null);
        }
    }

    /// <summary>Rethrows the failure of a streaming loop, if there was one. Called as the server is disposed.</summary>
    private void ThrowStreamFault()
    {
        Exception? fault = Interlocked.Exchange(ref _streamFault, null);
        if (fault is not null)
        {
            ExceptionDispatchInfo.Throw(fault);
        }
    }

    /// <summary>A running stream: whose command started it, how to stop it and what to wait for.</summary>
    private sealed class StreamRun(Connection? owner, CancellationTokenSource cancel)
    {
        public Connection? Owner { get; } = owner;

        public CancellationTokenSource Cancel { get; } = cancel;

        /// <summary>Completes when the loop has ended; never faults.</summary>
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    /// <summary>What a stream sends and where, settled and checked when it starts.</summary>
    private sealed record StreamPlan(
        IPEndPoint Target,
        SampleFormat Format,
        int PayloadSize,
        double SampleRate,
        int Channels,
        Pacing Pacing,
        FillSamples Source,
        Func<ushort, bool>? DropPacket,
        ReadOnlyMemory<byte>[]? Packets,
        bool Timestamps,
        DateTimeOffset? TimestampOrigin)
    {
        public static StreamPlan Create(IPEndPoint target, StreamOptions options, SampleFormat format, int payloadSize, double sampleRate)
        {
            if (target.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(target.Address))
            {
                throw new ArgumentException("The test server streams to IPv4 loopback addresses only.", nameof(target));
            }

            if (target.Port == 0)
            {
                throw new ArgumentException("The target port must not be 0.", nameof(target));
            }

            int sampleBytes = SampleSources.BytesPerSample(format);
            int trailer = options.Timestamps ? PacketTimestamp.Size : 0;
            ReadOnlyMemory<byte>[]? packets = null;
            if (options.Packets is not null)
            {
                packets = [.. options.Packets];
                if (packets.Length == 0)
                {
                    throw new ArgumentException("Packets must hold at least one packet.", nameof(options));
                }

                int unfit = Array.FindIndex(packets, packet =>
                    packet.Length < sampleBytes || packet.Length % sampleBytes != 0 || packet.Length + trailer > MaxPayloadSize);
                if (unfit >= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(options),
                        packets[unfit].Length,
                        $"Entry {unfit} of Packets is unusable: every entry must hold whole samples of {sampleBytes} bytes, at most {MaxPayloadSize - trailer} bytes.");
                }
            }
            else if (payloadSize < sampleBytes + trailer || payloadSize > MaxPayloadSize)
            {
                string least = trailer == 0 ? "one sample" : "one sample and the timestamp";
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    payloadSize,
                    $"PayloadSize must be from {sampleBytes + trailer} ({least}) to {MaxPayloadSize} bytes.");
            }

            if (!double.IsFinite(sampleRate) || sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), sampleRate, "SampleRate must be positive.");
            }

            if (options.Channels < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.Channels, "Channels must be at least 1.");
            }

            ArgumentNullException.ThrowIfNull(options.Source);
            return new StreamPlan(
                target,
                format,
                payloadSize,
                sampleRate,
                options.Channels,
                options.Pacing,
                options.Source,
                options.DropPacket,
                packets,
                options.Timestamps,
                options.TimestampOrigin);
        }
    }

    /// <summary>The sending side of one stream: its socket, the datagram buffer and the position in the stream.</summary>
    private sealed class DataStream : IDisposable
    {
        private readonly StreamPlan _plan;
        private readonly Socket _socket;
        private readonly SocketAddress _target;
        private readonly byte[] _datagram;
        private readonly int _sampleBytes;
        private readonly int _samplesPerPacket;
        private readonly int _trailerSize;
        private readonly DateTimeOffset _origin;
        private readonly SampleRateTimeScale _scale;

        private ushort _sequence;
        private long _firstSample;
        private int _nextPacket;

        public DataStream(StreamPlan plan, Socket socket)
        {
            _plan = plan;
            _socket = socket;
            _target = plan.Target.Serialize();
            _sampleBytes = SampleSources.BytesPerSample(plan.Format);
            _trailerSize = plan.Timestamps ? PacketTimestamp.Size : 0;
            _samplesPerPacket = (plan.PayloadSize - _trailerSize) / _sampleBytes;
            _origin = plan.TimestampOrigin ?? DateTimeOffset.UtcNow;
            _scale = new SampleRateTimeScale(plan.SampleRate);

            // Without Packets the bytes between the samples and the timestamp stay zero for good: only whole samples
            // are ever written, and the timestamp always lands in the last bytes.
            int largest = plan.Packets is null ? plan.PayloadSize : plan.Packets.Max(packet => packet.Length) + _trailerSize;
            _datagram = new byte[DatagramPrefixSize + largest];
        }

        public void Dispose() => _socket.Dispose();

        /// <summary>Sends packets until <paramref name="token"/> is cancelled.</summary>
        public async Task RunAsync(CancellationToken token)
        {
            // A packet is due when its first sample is, per channel, at the sample rate; packets of any size keep the pace.
            double ticksPerSample = Stopwatch.Frequency / (double)_plan.Channels / _plan.SampleRate;
            long pauseThreshold = Stopwatch.Frequency / 1000;
            long start = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                if (_plan.Pacing == Pacing.RealTime)
                {
                    // A pause shorter than a millisecond is not worth one: the packet goes now, at most a millisecond
                    // early. After a late wake-up the packets that are due go in a burst.
                    long now = Stopwatch.GetTimestamp();
                    long due = start + (long)(_firstSample * ticksPerSample);
                    if (due - now > pauseThreshold)
                    {
                        await Task.Delay(Stopwatch.GetElapsedTime(now, due), token).ConfigureAwait(false);
                    }
                }

                SendNext();
            }
        }

        /// <summary>Sends the packet of the current sequence number unless it is dropped, then moves on to the next one.</summary>
        private void SendNext()
        {
            ReadOnlyMemory<byte> recorded = _plan.Packets is { } packets ? packets[_nextPacket] : default;
            int samplesLength = _plan.Packets is null ? _samplesPerPacket * _sampleBytes : recorded.Length;
            int payloadSize = _plan.Packets is null ? _plan.PayloadSize : recorded.Length + _trailerSize;
            if (_plan.DropPacket?.Invoke(_sequence) != true)
            {
                int length = DatagramPrefixSize + payloadSize;

                // The header is written here and not by FrameHeader.Write: a length the 13-bit field cannot hold is encoded
                // as 0 (8194 is the specification's own use of that, longer jumbo datagrams follow the same convention).
                int encodedLength = length > FrameHeader.MaxEncodableLength ? 0 : length;
                BinaryPrimitives.WriteUInt16LittleEndian(_datagram, (ushort)(((int)ReplyType.Data0 << DataTypeShift) | encodedLength));
                BinaryPrimitives.WriteUInt16LittleEndian(_datagram.AsSpan(FrameHeader.Size), _sequence);

                Span<byte> payload = _datagram.AsSpan(DatagramPrefixSize, payloadSize);
                Span<byte> samples = payload[..samplesLength];
                if (_plan.Packets is null)
                {
                    samples.Clear();
                    _plan.Source(samples, _firstSample, _plan.Format);
                }
                else
                {
                    recorded.Span.CopyTo(samples);
                }

                if (_trailerSize > 0)
                {
                    PacketTimestamp.AtSample(_origin, _firstSample / _plan.Channels, _scale).Write(payload[^_trailerSize..]);
                }

                try
                {
                    _socket.SendTo(_datagram.AsSpan(0, length), SocketFlags.None, _target);
                }
                catch (SocketException)
                {
                    // UDP does not promise delivery: a receiver that is gone (ICMP port unreachable) is not an error.
                }
            }

            _sequence = DataSequence.Next(_sequence);
            _firstSample += samplesLength / _sampleBytes;
            if (_plan.Packets is { } all)
            {
                _nextPacket = (_nextPacket + 1) % all.Length;
            }
        }
    }
}
