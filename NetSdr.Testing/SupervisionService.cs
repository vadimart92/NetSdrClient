using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetSdr.Testing;

/// <summary>
/// The service channel of an emulated receiver, as a microcontroller next to it would offer: a TCP server on loopback
/// that reads JSON objects one after another and runs the handler set for its command, the string in
/// <see cref="CommandField"/>. A request ends where its object closes: no line break is needed after it, and whitespace
/// between requests is skipped. Something that is not JSON has no end of its own, so it ends at the next line break. The handler decides everything else: what to write back and when, whether to reboot
/// the receiver (<see cref="RebootAsync"/>), change <see cref="NetSdrTestServer.Availability"/>, or break the
/// connection. A connection may send any number of requests; they are handled one at a time, in order, and several
/// connections are served at once. The service does not own <see cref="Receiver"/>; dispose both.
/// </summary>
public sealed class SupervisionService : IAsyncDisposable
{
    // A request this long is not one: the connection is dropped instead of buffering without end.
    private const int MaxRequestLength = 64 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();

    // Guarded by _sync. Handlers always run outside it, so a handler may call back into the service.
    private readonly Dictionary<string, Func<ServiceContext, Task>> _handlers = new(StringComparer.Ordinal);
    private readonly List<ServiceRequest> _requests = [];
    private readonly List<Exception> _handlerErrors = [];
    private readonly List<ServiceConnection> _connections = [];
    private readonly List<Task> _serving = [];
    private Func<ServiceContext, Task> _unhandled = ReplyUnhandledAsync;
    private string _commandField = "cmd";
    private Task _boot = Task.CompletedTask;

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _disposal;

    /// <param name="receiver">The emulated receiver the service controls.</param>
    public SupervisionService(NetSdrTestServer receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        Receiver = receiver;
    }

    /// <summary>The emulated receiver the service controls.</summary>
    public NetSdrTestServer Receiver { get; }

    /// <summary>The TCP port the service listens on; 0 until <see cref="StartAsync"/> has been called.</summary>
    public int Port { get; private set; }

    /// <summary>The field of a request that holds its command; <c>"cmd"</c> by default, matched exactly.</summary>
    /// <exception cref="ArgumentException">The value is null or empty.</exception>
    public string CommandField
    {
        get
        {
            lock (_sync)
            {
                return _commandField;
            }
        }

        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            lock (_sync)
            {
                _commandField = value;
            }
        }
    }

    /// <summary>A snapshot of every request received so far, oldest first, whether it was valid or not. Whitespace between requests is skipped.</summary>
    public IReadOnlyList<ServiceRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// A snapshot of the exceptions thrown by handlers, and by the <c>onBoot</c> of a reboot, oldest first. A handler
    /// that throws ends its connection, as a crashed microcontroller would; the service goes on.
    /// </summary>
    public IReadOnlyList<Exception> HandlerErrors
    {
        get
        {
            lock (_sync)
            {
                return [.. _handlerErrors];
            }
        }
    }

    /// <summary>Whether a reboot started by <see cref="RebootAsync"/> is still running.</summary>
    public bool IsBooting
    {
        get
        {
            lock (_sync)
            {
                return !_boot.IsCompleted;
            }
        }
    }

    /// <summary>Starts listening on loopback.</summary>
    /// <param name="port">The port to listen on; 0 lets the system choose, see <see cref="Port"/>.</param>
    /// <exception cref="InvalidOperationException">The service has already been started.</exception>
    /// <exception cref="ObjectDisposedException">The service has been disposed.</exception>
    public Task StartAsync(int port = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_listener is not null)
            {
                throw new InvalidOperationException("The service has already been started.");
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
    /// Sets the handler for <paramref name="command"/>, replacing an earlier one. It runs on the connection's task, one
    /// request of that connection at a time; handlers of different connections may run at once.
    /// </summary>
    public void On(string command, Func<ServiceContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _handlers[command] = handler;
        }
    }

    /// <summary>
    /// Sets the handler for a request without a handler of its own: a command nobody handles, and a request that is not a
    /// JSON object or has no command (<see cref="ServiceRequest.Command"/> is null then). By default the first is
    /// answered with <c>{"error":"unknown command"}</c> and the second with <c>{"error":"bad request"}</c>.
    /// </summary>
    public void OnUnhandled(Func<ServiceContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _unhandled = handler;
        }
    }

    /// <summary>
    /// Reboots the receiver: it closes every new connection (<see cref="ServerAvailability.CloseOnAccept"/>), drops the
    /// client it serves, stops the stream and, with <paramref name="clearState"/>, forgets what was set and preloaded.
    /// After <paramref name="bootTime"/> it runs <paramref name="onBoot"/>, for example to preload the items of a fresh
    /// device, and accepts connections again; an availability that <paramref name="onBoot"/> or the test set during
    /// the boot is kept, so a receiver can come back hung. Completes with <see langword="true"/> once the receiver is
    /// back, or at once with <see langword="false"/> while another reboot runs, which is left alone. The reboot is not
    /// tied to the connection that asked for it: it goes on when that connection closes. An exception from
    /// <paramref name="onBoot"/> is recorded in <see cref="HandlerErrors"/>; the receiver comes back all the same.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bootTime"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The service has been disposed.</exception>
    public Task<bool> RebootAsync(TimeSpan bootTime, bool clearState = false, Action<NetSdrTestServer>? onBoot = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bootTime, TimeSpan.Zero);
        Task boot;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (!_boot.IsCompleted)
            {
                return Task.FromResult(false);
            }

            boot = Task.Run(() => BootAsync(bootTime, clearState, onBoot));
            _boot = boot;
        }

        return WaitForBootAsync(boot);
    }

    /// <summary>Stops listening, closes every connection and waits for the handlers and a running reboot to end.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposal ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposal);
        }
    }

    private static async Task<bool> WaitForBootAsync(Task boot)
    {
        await boot.ConfigureAwait(false);
        return true;
    }

    private static Task ReplyUnhandledAsync(ServiceContext context) =>
        context.ReplyAsync(new { Error = context.Request.Command is null ? "bad request" : "unknown command" });

    /// <summary>One reboot. It never faults: a failing <c>onBoot</c> is recorded, and the receiver always comes back.</summary>
    private async Task BootAsync(TimeSpan bootTime, bool clearState, Action<NetSdrTestServer>? onBoot)
    {
        // Connections are refused first, so the dropped client cannot slip back in before the boot.
        Receiver.Availability = ServerAvailability.CloseOnAccept;
        try
        {
            await Receiver.DisconnectClientAsync().ConfigureAwait(false);
            await Receiver.StopStreamingAsync().ConfigureAwait(false);
            if (clearState)
            {
                Receiver.ClearState();
            }

            await Task.Delay(bootTime, _lifetime.Token).ConfigureAwait(false);
            onBoot?.Invoke(Receiver);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The service is being disposed; the receiver comes back without its onBoot.
        }
        catch (Exception ex)
        {
            Record(ex);
        }
        finally
        {
            if (Receiver.Availability == ServerAvailability.CloseOnAccept)
            {
                Receiver.Availability = ServerAvailability.Normal;
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        TcpListener? listener;
        Task? acceptLoop;
        ServiceConnection[] connections;
        lock (_sync)
        {
            listener = _listener;
            acceptLoop = _acceptLoop;
            connections = [.. _connections];
        }

        // Cancel before closing anything, so the loops take the failures that follow for the shutdown.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        foreach (ServiceConnection connection in connections)
        {
            connection.Close();
        }

        if (acceptLoop is not null)
        {
            await acceptLoop.ConfigureAwait(false);
        }

        Task[] serving;
        Task boot;
        lock (_sync)
        {
            serving = [.. _serving];
            boot = _boot;
        }

        await Task.WhenAll(serving).ConfigureAwait(false);
        await boot.ConfigureAwait(false);
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
                    _serving.RemoveAll(task => task.IsCompleted);
                    _serving.Add(Task.Run(() => ServeAsync(socket)));
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // The service is being disposed.
        }
    }

    /// <summary>Serves one client until it leaves, a handler closes the connection or the service is disposed.</summary>
    private async Task ServeAsync(Socket socket)
    {
        ServiceConnection connection;
        try
        {
            connection = new ServiceConnection(socket, _lifetime.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The client dropped before it could be served.
            socket.Dispose();
            return;
        }

        lock (_sync)
        {
            if (_disposal is not null)
            {
                connection.Dispose();
                return;
            }

            _connections.Add(connection);
        }

        try
        {
            await ReadRequestsAsync(connection).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client left, a handler closed the connection or the service is shutting down.
        }
        finally
        {
            lock (_sync)
            {
                _connections.Remove(connection);
            }

            connection.Dispose();
        }
    }

    private async Task ReadRequestsAsync(ServiceConnection connection)
    {
        PipeReader reader = PipeReader.Create(connection.Stream);
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(connection.Token).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                while (TryTakeRequest(ref buffer, out string text))
                {
                    await HandleRequestAsync(connection, text).ConfigureAwait(false);
                    if (connection.IsClosed)
                    {
                        return;
                    }
                }

                if (buffer.Length > MaxRequestLength)
                {
                    return;
                }

                // Everything buffered has been examined, so the next read waits for more bytes. An object that is not
                // closed is not a request: a client that leaves in the middle of one has not sent it.
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits the next request off <paramref name="buffer"/>, skipping the whitespace before it: a JSON object or array
    /// up to where it closes, or, for anything else, up to the next line break, without it.
    /// </summary>
    private static bool TryTakeRequest(ref ReadOnlySequence<byte> buffer, out string text)
    {
        text = string.Empty;
        buffer = SkipWhitespace(buffer);
        if (buffer.IsEmpty)
        {
            return false;
        }

        byte first = buffer.FirstSpan[0];
        if (first is (byte)'{' or (byte)'[' && TryFindValueEnd(buffer, out SequencePosition? valueEnd))
        {
            if (valueEnd is null)
            {
                return false;
            }

            text = Encoding.UTF8.GetString(buffer.Slice(0, valueEnd.Value));
            buffer = buffer.Slice(valueEnd.Value);
            return true;
        }

        SequencePosition? lineEnd = buffer.PositionOf((byte)'\n');
        if (lineEnd is null)
        {
            return false;
        }

        text = Encoding.UTF8.GetString(buffer.Slice(0, lineEnd.Value)).TrimEnd('\r');
        buffer = buffer.Slice(buffer.GetPosition(1, lineEnd.Value));
        return true;
    }

    /// <summary>
    /// Finds where the object or array at the start of <paramref name="buffer"/> closes; <paramref name="end"/> is null
    /// while it is still open. False when it is not valid JSON, so it has no end of its own.
    /// </summary>
    private static bool TryFindValueEnd(ReadOnlySequence<byte> buffer, out SequencePosition? end)
    {
        end = null;
        var reader = new Utf8JsonReader(buffer, isFinalBlock: false, state: default);
        try
        {
            if (reader.Read() && reader.TrySkip())
            {
                end = reader.Position;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ReadOnlySequence<byte> SkipWhitespace(ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);
        reader.AdvancePastAny((byte)' ', (byte)'\t', (byte)'\r', (byte)'\n');
        return buffer.Slice(reader.Position);
    }

    private async Task HandleRequestAsync(ServiceConnection connection, string text)
    {
        ServiceRequest request;
        Func<ServiceContext, Task>? handler;
        lock (_sync)
        {
            request = ServiceRequest.Parse(text, _commandField);
            _requests.Add(request);
            if (request.Command is null || !_handlers.TryGetValue(request.Command, out handler))
            {
                handler = _unhandled;
            }
        }

        try
        {
            await handler(new ServiceContext(this, request, connection)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connection.Token.IsCancellationRequested)
        {
            // The connection closed while the handler waited on its token.
        }
        catch (Exception ex)
        {
            Record(ex);
            connection.Close();
        }
    }

    private void Record(Exception ex)
    {
        lock (_sync)
        {
            _handlerErrors.Add(ex);
        }
    }
}
