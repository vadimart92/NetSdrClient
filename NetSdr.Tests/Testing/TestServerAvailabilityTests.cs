using System.Net;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class TestServerAvailabilityTests
{
    private static NetSdrControlClientOptions Quick() => new() { ResponseTimeout = TimeSpan.FromMilliseconds(200), FaultOnTimeout = false };

    [Test]
    public async Task CloseOnAccept_NewConnectionClosed_CurrentKept()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        server.Availability = ServerAvailability.CloseOnAccept;
        Assert.That(
            (await client.SetAsync(new AfGain(0, 7)).WaitAsync(Limits.Test)).Level,
            Is.EqualTo(7));     // the current client is served
        await client.DisposeAsync();                                                                 // the server moves on to the next socket
        await using var next = new NetSdrControlClient();
        await next.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)).WaitAsync(Limits.Test);
        await Assert.ThatAsync(
            () => next.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test),
            Throws.InstanceOf<Exception>());   // closed before it was read
        await Eventually.ThatAsync(() => !next.IsConnected);
        Assert.That(server.Received.Select(r => r.Code), Does.Not.Contain(InterfaceVersion.Code));
    }

    [Test]
    public async Task Silent_RequestsRecorded_NoReplies()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)), Quick());
        await using var _ = server;
        await using var __ = client;
        server.Availability = ServerAvailability.Silent;                                             // acts on the current connection
        await Assert.ThatAsync(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        Assert.That(server.Received.Sole().Code, Is.EqualTo(InterfaceVersion.Code));
        await server.SendUnsolicitedAsync(new AfGain(0, 1));                                         // explicit frames still go out
        Assert.That((await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).As<AfGain>().Level, Is.EqualTo(1));
    }

    [Test]
    public async Task BackToNormal_ServesAgain()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)), Quick());
        await using var _ = server;
        await using var __ = client;
        Assert.That(server.Availability, Is.EqualTo(ServerAvailability.Normal));
        server.Availability = ServerAvailability.Silent;
        await Assert.ThatAsync(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        server.Availability = ServerAvailability.Normal;
        Assert.That((await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
    }

    [Test]
    public async Task ClearState_RemovesPreloads()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
        await using var _ = server;
        await using var __ = client;
        await client.SetAsync(new AfGain(0, 7));
        server.ClearState();
        await Assert.ThatAsync(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test), Throws.TypeOf<NetSdrNakException>());
        await Assert.ThatAsync(() => client.GetAsync<AfGain, byte>(0).WaitAsync(Limits.Test), Throws.TypeOf<NetSdrNakException>());
        Assert.That(server.Received, Has.Count.EqualTo(3));                                          // Received is kept
    }
}
