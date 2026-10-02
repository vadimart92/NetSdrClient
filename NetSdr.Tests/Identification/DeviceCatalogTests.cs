using System.Net;
using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Identification;

public class DeviceCatalogTests
{
    sealed record Dev(string Kind, NetSdrControlClient Client, DeviceIdentity Identity) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    static async Task<NetSdrTestServer> ServerAsync(Action<NetSdrTestServer>? setup = null)
    {
        var server = new NetSdrTestServer();
        setup?.Invoke(server);
        await server.StartAsync();
        return server;
    }

    static IPEndPoint At(NetSdrTestServer server) => new(IPAddress.Loopback, server.Port);

    [Fact]
    public async Task FirstMatchWins()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>()
            .Register("a", _ => true, (c, id) => new Dev("a", c, id))
            .Register("b", _ => true, (c, id) => new Dev("b", c, id));
        await using var device = await catalog.ConnectAsync(At(server));
        Assert.Equal("a", device.Kind);
    }

    [Fact]
    public async Task NoMatchNoDefault_Throws_AndClosesClient()
    {
        await using var server = await ServerAsync(s => s.Preload(new TargetName("NetSDR")));
        var catalog = new DeviceCatalog<Dev>()
            .Register("x", _ => false, (c, id) => new Dev("x", c, id))
            .Register("y", _ => false, (c, id) => new Dev("y", c, id));
        var ex = await Assert.ThrowsAsync<DeviceNotRecognizedException>(() => catalog.ConnectAsync(At(server)));
        Assert.Equal(new[] { "x", "y" }, ex.Candidates);
        Assert.Equal("NetSDR", ex.Identity.Name);
        await Loopback.AssertServerFreeAsync(server);
    }

    [Fact]
    public async Task NoMatch_UsesDefault_SecondDefaultReplacesFirst()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>()
            .Register("x", _ => false, (c, id) => new Dev("x", c, id))
            .Default((c, id) => new Dev("first", c, id))
            .Default((c, id) => new Dev("second", c, id));
        await using var device = await catalog.ConnectAsync(At(server));
        Assert.Equal("second", device.Kind);
        Assert.True(catalog.HasDefault);
        Assert.Equal(new[] { "x" }, catalog.Registrations);
    }

    [Fact]
    public async Task FactoryThrows_PropagatesAndClosesClient()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>().Register("boom", _ => true, (c, id) => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ConnectAsync(At(server)));
        await Loopback.AssertServerFreeAsync(server);
    }

    [Fact]
    public async Task PredicateThrows_PropagatesAndClosesClient()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>().Register("needs fact", id => id.Get<Version>().Major > 1, (c, id) => new Dev("v", c, id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.ConnectAsync(At(server)));
        await Loopback.AssertServerFreeAsync(server);
    }

    [Fact]
    public async Task ProbeTimeout_ClosesClient()
    {
        await using var server = await ServerAsync(s => s.OnRequest<TargetName>(_ => ControlReply.Silent));
        var catalog = new DeviceCatalog<Dev>(clientOptions: new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) })
            .Register("any", _ => true, (c, id) => new Dev("any", c, id));
        await Assert.ThrowsAsync<TimeoutException>(() => catalog.ConnectAsync(At(server)));
        await Loopback.AssertServerFreeAsync(server);
    }

    [Fact]
    public async Task Attach_NoMatch_KeepsClientOpen()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(9)));
        await using var _ = server; await using var __ = client;
        var catalog = new DeviceCatalog<Dev>().Register("x", _ => false, (c, id) => new Dev("x", c, id));
        await Assert.ThrowsAsync<DeviceNotRecognizedException>(() => catalog.AttachAsync(client));
        Assert.Equal(9u, (await client.GetAsync<ProductId>()).Value);
    }

    [Fact]
    public async Task Attach_PredicateOrFactoryThrows_KeepsClientOpen()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(9)));
        await using var _ = server; await using var __ = client;
        var badPredicate = new DeviceCatalog<Dev>().Register("p", id => id.Get<Version>().Major > 1, (c, id) => new Dev("p", c, id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => badPredicate.AttachAsync(client));
        var badFactory = new DeviceCatalog<Dev>().Register("f", _ => true, (c, id) => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => badFactory.AttachAsync(client));
        Assert.Equal(9u, (await client.GetAsync<ProductId>()).Value);
    }

    [Fact]
    public async Task Attach_Match_HandsTheClientToTheFactory()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        var catalog = new DeviceCatalog<Dev>().Register("any", _ => true, (c, id) => new Dev("any", c, id));
        var device = await catalog.AttachAsync(client);
        Assert.Same(client, device.Client);
    }

    [Fact]
    public async Task CancelDuringIdentification_ClosesClient()
    {
        await using var server = await ServerAsync(s => s.OnRequest<TargetName>(_ => ControlReply.Silent));
        var catalog = new DeviceCatalog<Dev>(clientOptions: new NetSdrControlClientOptions { ResponseTimeout = Limits.Test })
            .Register("any", _ => true, (c, id) => new Dev("any", c, id));
        using var cts = new CancellationTokenSource();
        var connect = catalog.ConnectAsync(At(server), cts.Token);
        await Eventually.ThatAsync(() => server.Received.Count > 0);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Limits.Test));
        await Loopback.AssertServerFreeAsync(server);
    }

    [Fact]
    public void NotRecognizedException_NamesTheDeviceAndTheCandidates()
    {
        var candidates = new[] { "Vega v2", "Vega v1" };
        var unnamed = new DeviceNotRecognizedException(new DeviceIdentityBuilder().Current, candidates);
        Assert.Equal("Device was not recognized (name: ?, product id: ?); candidates: Vega v2, Vega v1.", unnamed.Message);

        var builder = new DeviceIdentityBuilder { Name = "NetSDR", ProductId = 0x03524453 };
        var named = new DeviceNotRecognizedException(builder.Current, candidates);
        Assert.Equal("Device was not recognized (name: NetSDR, product id: 03524453); candidates: Vega v2, Vega v1.", named.Message);
        Assert.Equal(candidates, named.Candidates);
        Assert.Empty(new DeviceNotRecognizedException(builder.Current, Array.Empty<string>()).Candidates);
    }

    [Fact]
    public async Task AsyncFactory_ReceivesCallerToken()
    {
        await using var server = await ServerAsync();
        var factoryStarted = new TaskCompletionSource();
        var catalog = new DeviceCatalog<Dev>().Register("slow", _ => true, async (c, id, ct) =>
        {
            factoryStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new Dev("slow", c, id);
        });
        using var cts = new CancellationTokenSource();
        var connect = catalog.ConnectAsync(At(server), cts.Token);
        await factoryStarted.Task.WaitAsync(Limits.Test);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Limits.Test));
        await Loopback.AssertServerFreeAsync(server);
    }

    [Fact]
    public async Task ParallelConnects_GiveIndependentDevices()
    {
        await using var first = await ServerAsync(s => s.Preload(new ProductId(1)));
        await using var second = await ServerAsync(s => s.Preload(new ProductId(2)));
        var catalog = new DeviceCatalog<Dev>().Register("any", _ => true, (c, id) => new Dev("any", c, id));
        var devices = await Task.WhenAll(catalog.ConnectAsync(At(first)), catalog.ConnectAsync(At(second))).WaitAsync(Limits.Test);
        await using var a = devices[0]; await using var b = devices[1];
        Assert.Equal((1u, 2u), (a.Identity.ProductId!.Value, b.Identity.ProductId!.Value));
        Assert.NotSame(a.Client, b.Client);
    }

    [Fact]
    public async Task CatalogProbe_RunsAfterStandardProbes()
    {
        await using var server = await ServerAsync(s => s.Preload(new RfGain(0, -10)));
        var catalog = new DeviceCatalog<Dev>(new IdentificationOptions { Probes = { Probes.Item<RfGain, byte>(0) } })
            .Register("any", _ => true, (c, id) => new Dev("any", c, id));
        await using var device = await catalog.ConnectAsync(At(server));
        var codes = server.Received.Select(r => r.Code).ToArray();
        Assert.Equal(new ushort[] { 0x000A, 0x0038 }, codes[^2..]);
        Assert.Equal(-10, device.Identity.Get<RfGain>().GainDb);
    }
}
