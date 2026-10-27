using System.Net;
using System.Net.Sockets;

namespace NetSdr.Testing;

/// <summary>One accepted client of a <see cref="NetSdrServiceServer"/>: its stream, a token that ends with it, and how it ends.</summary>
internal sealed class ServiceConnection : IDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _closed;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _isClosed;

    public ServiceConnection(Socket socket, CancellationToken serverToken)
    {
        socket.NoDelay = true;
        _socket = socket;
        RemoteEndPoint = (IPEndPoint?)socket.RemoteEndPoint;
        Stream = new NetworkStream(socket, ownsSocket: true);
        _closed = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        Token = _closed.Token;
    }

    public NetworkStream Stream { get; }

    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>Cancelled when the connection is closed or the server is disposed.</summary>
    public CancellationToken Token { get; }

    public bool IsClosed => Volatile.Read(ref _isClosed) != 0;

    /// <summary>Writes the bytes whole; writes from several tasks do not interleave.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Never cancelled: an interrupted write would leave half a line on the wire. Closing the stream ends it.
            await Stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Ends the connection the ordinary way: what has been written is delivered, then the stream ends.</summary>
    public void Close() => End(reset: false);

    /// <summary>Ends the connection with a reset: the client's next read fails, unread data may be lost.</summary>
    public void Abort() => End(reset: true);

    public void Dispose()
    {
        Close();
        _closed.Dispose();
    }

    private void End(bool reset)
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0)
        {
            return;
        }

        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, so already closed.
        }

        if (reset)
        {
            try
            {
                // A zero linger turns the close into a reset. The socket is closed here, not by the stream: disposing the
                // stream shuts the socket down first, and that ends the connection the ordinary way.
                _socket.LingerState = new LingerOption(true, 0);
                _socket.Dispose();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // The socket is already gone; there is nothing left to reset.
            }
        }

        Stream.Dispose();
    }
}
