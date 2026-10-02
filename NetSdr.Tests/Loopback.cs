using System.Net;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Testing;

namespace NetSdr.Tests;

/// <summary>A test server and a client connected to each other over loopback.</summary>
internal static class Loopback
{
    public static async Task<(NetSdrTestServer Server, NetSdrControlClient Client)> StartAsync(
        Action<NetSdrTestServer>? setup = null, NetSdrControlClientOptions? options = null)
    {
        var server = new NetSdrTestServer();
        setup?.Invoke(server);
        await server.StartAsync();
        var client = new NetSdrControlClient(options);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        return (server, client);
    }

    // The server serves one client at a time, so a NAK for a fresh client proves the previous one is gone.
    public static async Task AssertServerFreeAsync(NetSdrTestServer server)
    {
        await using var next = new NetSdrControlClient();
        await next.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        await Assert.ThrowsAsync<NetSdrNakException>(
            () => next.SendAsync(RequestType.Get, 0x7FFF, ReadOnlyMemory<byte>.Empty).WaitAsync(Limits.Test));
    }
}
