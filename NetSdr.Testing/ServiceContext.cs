using System.Net;
using System.Text;
using System.Text.Json;

namespace NetSdr.Testing;

/// <summary>
/// What a handler of <see cref="NetSdrServiceServer"/> gets for one request: the request, the receiver behind the
/// service, and the connection it came on. Nothing is written unless the handler writes it, and the order of the
/// handler is the order on the wire: reply first and reboot after, or the other way round.
/// </summary>
public sealed class ServiceContext
{
    private readonly ServiceConnection _connection;

    internal ServiceContext(NetSdrServiceServer service, ServiceRequest request, ServiceConnection connection)
    {
        Service = service;
        Request = request;
        _connection = connection;
    }

    public ServiceRequest Request { get; }

    public NetSdrServiceServer Service { get; }

    /// <summary>The emulated receiver the service controls.</summary>
    public NetSdrTestServer Receiver => Service.Receiver;

    /// <summary>The client end of the connection; <see langword="null"/> when it is unknown.</summary>
    public IPEndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

    /// <summary>Cancelled when this connection is closed, by either side, or the service is disposed.</summary>
    public CancellationToken Token => _connection.Token;

    /// <summary>
    /// Writes <paramref name="value"/> as one line of JSON, with the web defaults of System.Text.Json (camelCase names),
    /// so <c>new { Status = "ok" }</c> is written as <c>{"status":"ok"}</c>. May be called any number of times.
    /// </summary>
    /// <exception cref="IOException">The connection has been closed.</exception>
    /// <exception cref="ObjectDisposedException">The connection has been closed.</exception>
    public Task ReplyAsync<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, NetSdrServiceServer.JsonOptions);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return _connection.WriteAsync(line);
    }

    /// <summary>
    /// Writes <paramref name="text"/> as it is, in UTF-8 and without a line break: half a line, a malformed reply, or
    /// several lines at once, for a device that does not follow its own protocol.
    /// </summary>
    /// <exception cref="IOException">The connection has been closed.</exception>
    /// <exception cref="ObjectDisposedException">The connection has been closed.</exception>
    public Task ReplyRawAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _connection.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Closes the connection the ordinary way, after what has been written: the client reads to the end of the stream.
    /// No further request on it is read.
    /// </summary>
    public void Close() => _connection.Close();

    /// <summary>
    /// Breaks the connection with a reset: the client's next read fails, and what it has not read yet may be lost.
    /// No further request on it is read.
    /// </summary>
    public void Abort() => _connection.Abort();

    /// <summary>Reboots the receiver, see <see cref="NetSdrServiceServer.RebootAsync"/>.</summary>
    public Task<bool> RebootAsync(TimeSpan bootTime, bool clearState = false, Action<NetSdrTestServer>? onBoot = null) =>
        Service.RebootAsync(bootTime, clearState, onBoot);
}
