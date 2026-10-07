using System.Net;
using System.Net.Sockets;

namespace NetSdr.Examples.Vega.Tests;

/// <summary>
/// The service protocol of a Vega receiver on loopback: every connection sends 8 bytes, gets the reply of
/// <c>respond</c> and is closed. <c>respond</c> runs on the connection's task, outside the server's lock; a client that
/// leaves before the reply is ignored.
/// </summary>
public sealed class VegaServiceServer(Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> respond) : IAsyncDisposable
{
    private const int RequestSize = 8;

    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();

    // Guarded by _sync.
    private readonly List<byte[]> _requests = [];
    private readonly List<Task> _connections = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _disposal;

    /// <summary>The TCP port the server listens on; 0 until <see cref="StartAsync"/> has been called.</summary>
    public int Port { get; private set; }

    /// <summary>A snapshot of every complete request received so far, oldest first.</summary>
    public IReadOnlyList<byte[]> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Starts listening on loopback with a port the system chooses.</summary>
    public Task StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_listener is not null)
            {
                throw new InvalidOperationException("The server has already been started.");
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops listening and closes every connection being served.</summary>
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
        TcpListener? listener;
        Task? acceptLoop;
        lock (_sync)
        {
            listener = _listener;
            acceptLoop = _acceptLoop;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        if (acceptLoop is not null)
        {
            await acceptLoop.ConfigureAwait(false);
        }

        Task[] connections;
        lock (_sync)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        CancellationToken token = _lifetime.Token;
        try
        {
            while (true)
            {
                Socket socket = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
                lock (_sync)
                {
                    _connections.RemoveAll(task => task.IsCompleted);
                    _connections.Add(Task.Run(() => ServeAsync(socket, token)));
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // The server is being disposed.
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken token)
    {
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            var request = new byte[RequestSize];
            await stream.ReadExactlyAsync(request, token).ConfigureAwait(false);
            lock (_sync)
            {
                _requests.Add(request);
            }

            await stream.WriteAsync(respond(request), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client left before the reply, or the server is being disposed.
        }
        finally
        {
            socket.Dispose();
        }
    }
}
