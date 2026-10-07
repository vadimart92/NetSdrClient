using System.Net;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class TestServerAvailabilityTests
{
    static NetSdrControlClientOptions Quick() => new() { ResponseTimeout = TimeSpan.FromMilliseconds(200), FaultOnTimeout = false };

    [Fact]
    public async Task CloseOnAccept_NewConnectionClosed_CurrentKept()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        server.Availability = ServerAvailability.CloseOnAccept;
        Assert.Equal(7, (await client.SetAsync(new AfGain(0, 7)).WaitAsync(Limits.Test)).Level);     // the current client is served
        await client.DisposeAsync();                                                                 // the server moves on to the next socket
        await using var next = new NetSdrControlClient();
        await next.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)).WaitAsync(Limits.Test);
        await Assert.ThrowsAnyAsync<Exception>(() => next.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));   // closed before it was read
        await Eventually.ThatAsync(() => !next.IsConnected);
        Assert.DoesNotContain(server.Received, r => r.Code == InterfaceVersion.Code);
    }

    [Fact]
    public async Task Silent_RequestsRecorded_NoReplies()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)), Quick());
        await using var _ = server; await using var __ = client;
        server.Availability = ServerAvailability.Silent;                                             // acts on the current connection
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
        Assert.Equal(InterfaceVersion.Code, Assert.Single(server.Received).Code);
        await server.SendUnsolicitedAsync(new AfGain(0, 1));                                         // explicit frames still go out
        Assert.Equal(1, (await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).As<AfGain>().Level);
    }

    [Fact]
    public async Task BackToNormal_ServesAgain()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)), Quick());
        await using var _ = server; await using var __ = client;
        Assert.Equal(ServerAvailability.Normal, server.Availability);
        server.Availability = ServerAvailability.Silent;
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
        server.Availability = ServerAvailability.Normal;
        Assert.Equal(529, (await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test)).Version);
    }

    [Fact]
    public async Task ClearState_RemovesPreloads()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new AfGain(0, 7));
        server.ClearState();
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<AfGain, byte>(0).WaitAsync(Limits.Test));
        Assert.Equal(3, server.Received.Count);                                                      // Received is kept
    }
}
