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
        try
        {
            setup?.Invoke(server);
            await server.StartAsync();
            var client = new NetSdrControlClient(options);
            try
            {
                await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));

                // The connect completes before the server has accepted the client; a disconnect or an unsolicited
                // frame sent right away would find nobody to talk to.
                await server.ClientConnected.WaitAsync(Limits.Test);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            return (server, client);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
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
