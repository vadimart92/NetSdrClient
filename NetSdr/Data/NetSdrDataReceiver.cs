using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetSdr.Framing;

namespace NetSdr.Data;

/// <summary>
/// Receives the NetSDR data channel: UDP datagrams, each a 2-byte header, a 2-byte sequence number and samples.
/// A dedicated background thread reads the socket and calls the handler for every accepted datagram, so
/// the handler runs on that thread. Reception stops only when the receiver is disposed.
/// </summary>
/// <remarks>
/// Logging (<see cref="DataReceiverOptions.LoggerFactory"/>) writes nothing per packet: the start, the stop with the
/// totals, each sequence gap, the first handler error of an interval and a summary every
/// <see cref="DataReceiverOptions.StatisticsLogInterval"/>, checked once every 256 datagrams.
/// </remarks>
public sealed class NetSdrDataReceiver : IDisposable
{
    private const int MaxDatagramSize = 65535;
    private const int PrefixSize = FrameHeader.Size + sizeof(ushort);
    // Where the IPv4 address sits in the byte buffer of an IPv4 SocketAddress (after the family and the port).
    private const int AddressOffset = 4;
    private const int AddressSize = 4;

    /// <summary>
    /// How far behind the expected packet a packet may fall and still be taken for a late one (reordered or
    /// duplicated) rather than for a jump forward: a packet fewer than this many packets behind is delivered with
    /// no gap and the expectation stays. 1024 packets are about 130 ms of a stream of 7.8k packets a second
    /// (2 MS/s of 16-bit complex samples), far more than the network reorders. Anything else that is not the
    /// expected packet is a forward gap, however large: a window of half a cycle (0x8000) would hide every loss
    /// after an outage of that many packets, as the stream would look reordered until the numbers wrapped around.
    /// </summary>
    private const int ReorderWindow = 1024;

    /// <summary>How many datagrams, accepted or rejected, the receive thread handles between two looks at the clock.</summary>
    private const int SummaryCheckInterval = 256;

    private enum State
    {
        Created,
        Bound,
        Started,
        Disposed,
    }

    private readonly DataPacketHandler _handler;
    private readonly bool _validateLength;
    private readonly byte[]? _remoteAddress;
    private readonly ThreadPriority _threadPriority;
    private readonly Socket _socket;
    private readonly Lock _sync = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _statisticsLogInterval;
    private readonly bool _summaries;

    // Written under _sync; _state is also read by the receive thread without the lock.
    private volatile State _state;
    private IPEndPoint? _localEndPoint;
    private Thread? _thread;

    // Written by Start under _sync before the receive thread starts; read by Dispose for the totals of event 1203.
    private long _startedAt;

    // Touched only by the receive thread, except that Start sets _intervalStart before the thread starts.
    private bool _hasExpected;
    private ushort _expected;
    private int _sinceCheck;
    private long _intervalStart;
    private DataReceiverStatistics _intervalBase;
    private bool _handlerErrorLogged;

    // Written only by the receive thread, read by any thread through Statistics.
    private long _received;
    private long _bytes;
    private long _lost;
    private long _rejected;
    private long _handlerErrors;

    /// <summary>
    /// Creates the receiver and its IPv4 UDP socket, with the receive buffer set to
    /// <see cref="DataReceiverOptions.InitialReceiveBufferBytes"/>. Call <see cref="Bind(int)"/> and <see cref="Start"/> next.
    /// </summary>
    /// <param name="handler">Called on the receive thread for every accepted datagram.</param>
    /// <param name="options">Settings, copied here; the defaults when omitted.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="handler"/> or <see cref="DataReceiverOptions.LoggerFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><see cref="DataReceiverOptions.RemoteAddress"/> is not an IPv4 address.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="DataReceiverOptions.InitialReceiveBufferBytes"/> is negative, or
    /// <see cref="DataReceiverOptions.StatisticsLogInterval"/> is neither positive (up to <see cref="int.MaxValue"/>
    /// milliseconds) nor <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public NetSdrDataReceiver(DataPacketHandler handler, DataReceiverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new DataReceiverOptions();
        ArgumentNullException.ThrowIfNull(options.LoggerFactory, nameof(options));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options));
        TimeSpan interval = options.StatisticsLogInterval;
        if (interval != Timeout.InfiniteTimeSpan && (interval <= TimeSpan.Zero || interval.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                interval,
                "StatisticsLogInterval must be positive (at most Int32.MaxValue milliseconds) or Timeout.InfiniteTimeSpan.");
        }

        IPAddress? remote = options.RemoteAddress;
        if (remote is not null)
        {
            if (remote.IsIPv4MappedToIPv6)
            {
                remote = remote.MapToIPv4();
            }

            if (remote.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("RemoteAddress must be an IPv4 address.", nameof(options));
            }

            _remoteAddress = remote.GetAddressBytes();
        }

        _handler = handler;
        _validateLength = options.ValidateLength;
        _threadPriority = options.ThreadPriority;
        _logger = options.LoggerFactory.CreateLogger(typeof(NetSdrDataReceiver).FullName!);
        _timeProvider = options.TimeProvider;
        _statisticsLogInterval = interval;
        _summaries = interval != Timeout.InfiniteTimeSpan;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.ReceiveBufferSize = options.InitialReceiveBufferBytes;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <summary>The address and port the socket is bound to; the actual port when it was bound to port 0.</summary>
    /// <exception cref="InvalidOperationException">The receiver is not bound yet.</exception>
    /// <exception cref="ObjectDisposedException">The receiver is disposed.</exception>
    public IPEndPoint LocalEndPoint
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_state == State.Disposed, this);
                return _localEndPoint ?? throw new InvalidOperationException("The receiver is not bound; call Bind first.");
            }
        }
    }

    /// <summary>The socket receive buffer size (<c>SO_RCVBUF</c>), read back from the socket.</summary>
    /// <exception cref="ObjectDisposedException">The receiver is disposed.</exception>
    public int ActualReceiveBufferSize => _socket.ReceiveBufferSize;

    /// <summary>The counters so far; see <see cref="DataReceiverStatistics"/> for how consistent they are.</summary>
    public DataReceiverStatistics Statistics => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _bytes),
        Interlocked.Read(ref _lost),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _handlerErrors));

    /// <summary>Binds to <paramref name="localPort"/> on all IPv4 interfaces; 0 picks a free port.</summary>
    /// <exception cref="InvalidOperationException">The receiver is already bound.</exception>
    /// <exception cref="ObjectDisposedException">The receiver is disposed.</exception>
    /// <exception cref="SocketException">The port cannot be bound.</exception>
    public void Bind(int localPort = 0) => Bind(new IPEndPoint(IPAddress.Any, localPort));

    /// <summary>Binds to <paramref name="localEndPoint"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="localEndPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="localEndPoint"/> is not an IPv4 endpoint.</exception>
    /// <exception cref="InvalidOperationException">The receiver is already bound.</exception>
    /// <exception cref="ObjectDisposedException">The receiver is disposed.</exception>
    /// <exception cref="SocketException">The endpoint cannot be bound.</exception>
    public void Bind(IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        if (localEndPoint.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only an IPv4 endpoint is supported.", nameof(localEndPoint));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state == State.Disposed, this);
            if (_state != State.Created)
            {
                throw new InvalidOperationException("The receiver is already bound.");
            }

            _socket.Bind(localEndPoint);
            _localEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _state = State.Bound;
        }
    }

    /// <summary>
    /// Starts the receive thread. It runs until <see cref="Dispose"/>; there is no way to stop and restart.
    /// </summary>
    /// <exception cref="InvalidOperationException">The receiver is not bound or is already started.</exception>
    /// <exception cref="ObjectDisposedException">The receiver is disposed.</exception>
    public void Start()
    {
        IPEndPoint localEndPoint;
        int receiveBufferBytes;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state == State.Disposed, this);
            if (_state == State.Created)
            {
                throw new InvalidOperationException("The receiver is not bound; call Bind first.");
            }

            if (_state == State.Started)
            {
                throw new InvalidOperationException("The receiver is already started.");
            }

            localEndPoint = _localEndPoint!;
            receiveBufferBytes = _socket.ReceiveBufferSize;
            // Set before the thread starts, which publishes them to it.
            _startedAt = _timeProvider.GetTimestamp();
            _intervalStart = _startedAt;

            var thread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Priority = _threadPriority,
                Name = "NetSdr data receiver",
            };
            thread.Start();
            // Assigned before the lock is released, so a Dispose from a handler on the new thread sees it.
            _thread = thread;
            _state = State.Started;
        }

        DataReceiverLog.ReceiveStarted(_logger, localEndPoint, receiveBufferBytes);
    }

    /// <summary>
    /// Sets the socket receive buffer (<c>SO_RCVBUF</c>). The operating system may round or cap the value;
    /// <see cref="ActualReceiveBufferSize"/> tells what it applied.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The receiver is disposed.</exception>
    public void SetReceiveBuffer(int bytes) => _socket.ReceiveBufferSize = bytes;

    /// <summary>
    /// Sets the receive buffer to hold <paramref name="duration"/> of data arriving at <paramref name="bytesPerSecond"/>,
    /// rounded up and capped at <see cref="int.MaxValue"/>. See <see cref="DataRate.BytesPerSecond"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The resulting size is negative.</exception>
    /// <exception cref="ObjectDisposedException">The receiver is disposed.</exception>
    public void SetReceiveBuffer(TimeSpan duration, long bytesPerSecond) =>
        SetReceiveBuffer((int)Math.Min(int.MaxValue, Math.Ceiling(duration.TotalSeconds * bytesPerSecond)));

    /// <summary>
    /// Closes the socket and waits for the receive thread to finish, so no handler call is running when it returns.
    /// Called from the handler itself, it only closes the socket and the thread ends once the handler returns.
    /// Safe to call more than once; only the call that stops a started receiver logs its totals.
    /// </summary>
    public void Dispose()
    {
        State previous;
        Thread? thread;
        lock (_sync)
        {
            previous = _state;
            _state = State.Disposed;
            thread = _thread;
        }

        _socket.Dispose();

        if (thread is not null && !ReferenceEquals(thread, Thread.CurrentThread))
        {
            thread.Join();
        }

        if (previous == State.Started)
        {
            long now = _timeProvider.GetTimestamp();
            DataReceiverStatistics totals = Statistics;
            DataReceiverLog.ReceiveStopped(
                _logger,
                _localEndPoint,
                _timeProvider.GetElapsedTime(_startedAt, now),
                totals.Received,
                totals.Bytes,
                totals.Lost,
                totals.Rejected,
                totals.HandlerErrors);
        }
    }

    private static SampleFormat FormatOf(int datagramLength) => datagramLength switch
    {
        1028 or 516 => SampleFormat.Int16,
        1444 or 388 => SampleFormat.Int24,
        _ => SampleFormat.Unknown,
    };

    private void ReceiveLoop()
    {
        var buffer = new byte[MaxDatagramSize];
        var from = new SocketAddress(AddressFamily.InterNetwork);

        while (true)
        {
            int length;
            try
            {
                length = _socket.ReceiveFrom(buffer, SocketFlags.None, from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset && _state != State.Disposed)
            {
                // Windows reports an ICMP port-unreachable for an earlier send as a failed receive; the socket is fine.
                continue;
            }
            catch (SocketException e)
            {
                if (_state != State.Disposed)
                {
                    try
                    {
                        DataReceiverLog.ReceiveFailed(_logger, _localEndPoint, e);
                    }
                    catch (Exception)
                    {
                        // A logging provider failed; an unhandled exception here would end the process.
                    }
                }

                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Handle(buffer, length, from);

            // The datagram is counted and the handler has returned, so it belongs to the interval being checked.
            if (_summaries && ++_sinceCheck == SummaryCheckInterval)
            {
                _sinceCheck = 0;
                LogSummaryIfDue();
            }
        }
    }

    /// <summary>
    /// Reads the clock once and, when <see cref="DataReceiverOptions.StatisticsLogInterval"/> has passed since the
    /// last summary, logs what the counters gained since then and starts the next interval.
    /// </summary>
    private void LogSummaryIfDue()
    {
        long now = _timeProvider.GetTimestamp();
        // The two-argument overload: the one-argument one would read the clock a second time.
        TimeSpan elapsed = _timeProvider.GetElapsedTime(_intervalStart, now);
        if (elapsed < _statisticsLogInterval)
        {
            return;
        }

        // Only this thread writes the counters, so the totals are exact here.
        DataReceiverStatistics totals = Statistics;
        long received = totals.Received - _intervalBase.Received;
        long bytes = totals.Bytes - _intervalBase.Bytes;
        long lost = totals.Lost - _intervalBase.Lost;
        long rejected = totals.Rejected - _intervalBase.Rejected;
        long handlerErrors = totals.HandlerErrors - _intervalBase.HandlerErrors;
        try
        {
            if (lost > 0 || rejected > 0 || handlerErrors > 0)
            {
                DataReceiverLog.IntervalSummaryWithLoss(_logger, received, bytes, elapsed, lost, rejected, handlerErrors);
            }
            else
            {
                DataReceiverLog.IntervalSummary(_logger, received, bytes, elapsed);
            }
        }
        catch (Exception)
        {
            // A logging provider failed on the receive thread, where an unhandled exception would end the process;
            // the interval still turns over, and the next summary reports from here.
        }

        _intervalBase = totals;
        _intervalStart = now;
        _handlerErrorLogged = false;
    }

    private void Handle(byte[] buffer, int length, SocketAddress from)
    {
        if (_remoteAddress is not null
            && !from.Buffer.Span.Slice(AddressOffset, AddressSize).SequenceEqual(_remoteAddress))
        {
            Interlocked.Increment(ref _rejected);
            return;
        }

        var datagram = new ReadOnlySpan<byte>(buffer, 0, length);
        if (length < PrefixSize
            || !FrameHeader.TryRead(datagram, out int declaredLength, out byte type)
            || type != (byte)ReplyType.Data0
            || (_validateLength && declaredLength != length))
        {
            Interlocked.Increment(ref _rejected);
            return;
        }

        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(datagram[FrameHeader.Size..]);
        int gapBefore = 0;
        if (_hasExpected && sequence != 0)
        {
            // The sequence cycle is 0xFFFF packets long (0 is skipped), so a packet k behind the expected one is at
            // distance 0xFFFF - k.
            int distance = DataSequence.Distance(_expected, sequence);
            if (distance <= ushort.MaxValue - ReorderWindow)
            {
                gapBefore = distance;
                _expected = DataSequence.Next(sequence);
            }

            // Otherwise the packet is just behind the expected one: deliver it, but leave the expectation alone.
        }
        else
        {
            // The first packet joins the stream wherever it is, and a zero starts a new capture.
            _hasExpected = true;
            _expected = DataSequence.Next(sequence);
        }

        // Lost, then Bytes, then Received: Statistics reads them in the opposite order, so a reader that
        // sees this packet in Received also sees it in Bytes and Lost.
        if (gapBefore > 0)
        {
            Interlocked.Add(ref _lost, gapBefore);
        }

        ReadOnlySpan<byte> samples = datagram[PrefixSize..];
        Interlocked.Add(ref _bytes, samples.Length);
        Interlocked.Increment(ref _received);

        if (gapBefore > 0)
        {
            try
            {
                DataReceiverLog.SequenceGap(_logger, gapBefore, sequence);
            }
            catch (Exception)
            {
                // A logging provider failed; the gap is counted, and the packet is still delivered.
            }
        }

        var info = new DataPacketInfo(sequence, gapBefore, FormatOf(length));
        try
        {
            _handler(in info, samples);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _handlerErrors);
            if (!_handlerErrorLogged)
            {
                // Later errors are only counted until the next summary, or for the receiver's life without summaries.
                _handlerErrorLogged = true;
                try
                {
                    DataReceiverLog.HandlerFailed(_logger, sequence, e);
                }
                catch (Exception)
                {
                    // A logging provider failed; the error is counted, and the receive thread goes on.
                }
            }
        }
    }
}
