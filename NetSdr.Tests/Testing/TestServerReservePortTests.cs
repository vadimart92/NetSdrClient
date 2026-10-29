using System.Net;
using System.Net.Sockets;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class TestServerReservePortTests
{
    [Test]
    public async Task ReservePort_RefusesConnections_UntilStarted()
    {
        await using var server = new NetSdrTestServer();
        int port = server.ReservePort();
        Assert.That(port, Is.Not.Zero);
        Assert.That(server.Port, Is.EqualTo(port));

        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await Assert.ThatAsync(
                () => probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).WaitAsync(Limits.Test),
                Throws.TypeOf<SocketException>().With.Property(nameof(SocketException.SocketErrorCode)).EqualTo(SocketError.ConnectionRefused));
        }

        server.Preload(new InterfaceVersion(529));
        await server.StartAsync();
        Assert.That(server.Port, Is.EqualTo(port));
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).WaitAsync(Limits.Test);
        Assert.That((await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
    }

    [Test]
    public async Task ReservePort_HoldsThePort()
    {
        await using var server = new NetSdrTestServer();
        int port = server.ReservePort();
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.That(
            () => other.Bind(new IPEndPoint(IPAddress.Loopback, port)),
            Throws.TypeOf<SocketException>().With.Property(nameof(SocketException.SocketErrorCode)).EqualTo(SocketError.AddressAlreadyInUse));
    }

    [Test]
    public async Task Dispose_ReleasesTheReservedPort()
    {
        var server = new NetSdrTestServer();
        int port = server.ReservePort();
        await server.DisposeAsync();
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.That(() => other.Bind(new IPEndPoint(IPAddress.Loopback, port)), Throws.Nothing);
    }

    [Test]
    public async Task StartAsync_AcceptsTheReservedPort_RejectsAnother()
    {
        await using var server = new NetSdrTestServer();
        int port = server.ReservePort();
        await Assert.ThatAsync(() => server.StartAsync(port == ushort.MaxValue ? port - 1 : port + 1), Throws.ArgumentException);
        await server.StartAsync(port);
        Assert.That(server.Port, Is.EqualTo(port));
    }

    [Test]
    public async Task ReservePort_Twice_OrAfterStart_Throws()
    {
        await using var reserved = new NetSdrTestServer();
        reserved.ReservePort();
        Assert.That(() => reserved.ReservePort(), Throws.InvalidOperationException);

        await using var started = new NetSdrTestServer();
        await started.StartAsync();
        Assert.That(() => started.ReservePort(), Throws.InvalidOperationException);

        var disposed = new NetSdrTestServer();
        await disposed.DisposeAsync();
        Assert.That(() => disposed.ReservePort(), Throws.TypeOf<ObjectDisposedException>());
    }
}
