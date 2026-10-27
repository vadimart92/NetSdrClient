using System.Net;
using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Control;

namespace NetSdr.Tests.Identification;

public class DeviceCatalogTests
{
    private sealed record Dev(string Kind, INetSdrControlClient Client, DeviceIdentity Identity) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private static async Task<NetSdrTestServer> ServerAsync(Action<NetSdrTestServer>? setup = null)
    {
        var server = new NetSdrTestServer();
        setup?.Invoke(server);
        await server.StartAsync();
        return server;
    }

    private static IPEndPoint At(NetSdrTestServer server) => new(IPAddress.Loopback, server.Port);

    [Test]
    public async Task FirstMatchWins()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>()
            .Register("a", _ => true, (c, id) => new Dev("a", c, id))
            .Register("b", _ => true, (c, id) => new Dev("b", c, id));
        await using var device = await catalog.ConnectAsync(At(server));
        Assert.That(device.Kind, Is.EqualTo("a"));
    }

    [Test]
    public async Task NoMatchNoDefault_Throws_AndClosesClient()
    {
        await using var server = await ServerAsync(s => s.Preload(new TargetName("NetSDR")));
        var catalog = new DeviceCatalog<Dev>()
            .Register("x", _ => false, (c, id) => new Dev("x", c, id))
            .Register("y", _ => false, (c, id) => new Dev("y", c, id));
        var ex = await Assert.ThrowsAsync<DeviceNotRecognizedException>(() => catalog.ConnectAsync(At(server)));
        Assert.That(ex.Candidates, Is.EqualTo(new[] { "x", "y" }));
        Assert.That(ex.Identity.Name, Is.EqualTo("NetSDR"));
        await Loopback.AssertServerFreeAsync(server);
    }

    [Test]
    public async Task NoMatch_UsesDefault_SecondDefaultReplacesFirst()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>()
            .Register("x", _ => false, (c, id) => new Dev("x", c, id))
            .Default((c, id) => new Dev("first", c, id))
            .Default((c, id) => new Dev("second", c, id));
        await using var device = await catalog.ConnectAsync(At(server));
        Assert.That(device.Kind, Is.EqualTo("second"));
        Assert.That(catalog.HasDefault, Is.True);
        Assert.That(catalog.Registrations, Is.EqualTo(new[] { "x" }));
    }

    [Test]
    public async Task FactoryThrows_PropagatesAndClosesClient()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>().Register("boom", _ => true, (c, id) => throw new InvalidOperationException("boom"));
        await Assert.ThatAsync(() => catalog.ConnectAsync(At(server)), Throws.TypeOf<InvalidOperationException>());
        await Loopback.AssertServerFreeAsync(server);
    }

    [Test]
    public async Task PredicateThrows_PropagatesAndClosesClient()
    {
        await using var server = await ServerAsync();
        var catalog = new DeviceCatalog<Dev>().Register("needs fact", id => id.Get<Version>().Major > 1, (c, id) => new Dev("v", c, id));
        await Assert.ThatAsync(() => catalog.ConnectAsync(At(server)), Throws.TypeOf<KeyNotFoundException>());
        await Loopback.AssertServerFreeAsync(server);
    }

    [Test]
    public async Task ProbeTimeout_ClosesClient()
    {
        await using var server = await ServerAsync(s => s.OnRequest<TargetName>(_ => ControlReply.Silent));
        var catalog = new DeviceCatalog<Dev>(clientOptions: new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) })
            .Register("any", _ => true, (c, id) => new Dev("any", c, id));
        await Assert.ThatAsync(() => catalog.ConnectAsync(At(server)), Throws.TypeOf<TimeoutException>());
        await Loopback.AssertServerFreeAsync(server);
    }

    [Test]
    public async Task Attach_NoMatch_KeepsClientOpen()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(9)));
        await using var _ = server;
        await using var __ = client;
        var catalog = new DeviceCatalog<Dev>().Register("x", _ => false, (c, id) => new Dev("x", c, id));
        await Assert.ThatAsync(() => catalog.AttachAsync(client), Throws.TypeOf<DeviceNotRecognizedException>());
        Assert.That((await client.GetAsync<ProductId>()).Value, Is.EqualTo(9u));
    }

    [Test]
    public async Task Attach_PredicateOrFactoryThrows_KeepsClientOpen()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(9)));
        await using var _ = server;
        await using var __ = client;
        var badPredicate = new DeviceCatalog<Dev>().Register("p", id => id.Get<Version>().Major > 1, (c, id) => new Dev("p", c, id));
        await Assert.ThatAsync(() => badPredicate.AttachAsync(client), Throws.TypeOf<KeyNotFoundException>());
        var badFactory = new DeviceCatalog<Dev>().Register("f", _ => true, (c, id) => throw new InvalidOperationException("boom"));
        await Assert.ThatAsync(() => badFactory.AttachAsync(client), Throws.TypeOf<InvalidOperationException>());
        Assert.That((await client.GetAsync<ProductId>()).Value, Is.EqualTo(9u));
    }

    [Test]
    public async Task Attach_Match_HandsTheClientToTheFactory()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        var catalog = new DeviceCatalog<Dev>().Register("any", _ => true, (c, id) => new Dev("any", c, id));
        var device = await catalog.AttachAsync(client);
        Assert.That(device.Client, Is.SameAs(client));
    }

    // A factory that returns null breaks its contract. The error names the registration, or the default.

    [TestCase(false, "null maker")]
    [TestCase(true, "default")]
    public async Task FactoryReturnsNull_Connect_ThrowsAndClosesClient(bool viaDefault, string expectedName)
    {
        await using var server = await ServerAsync();
        var catalog = NullFactoryCatalog(viaDefault);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ConnectAsync(At(server)));
        Assert.That(ex.Message, Does.Contain(expectedName));
        await Loopback.AssertServerFreeAsync(server);
    }

    [TestCase(false, "null maker")]
    [TestCase(true, "default")]
    public async Task FactoryReturnsNull_Attach_ThrowsAndKeepsClientOpen(bool viaDefault, string expectedName)
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(9)));
        await using var _ = server;
        await using var __ = client;
        var catalog = NullFactoryCatalog(viaDefault);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.AttachAsync(client));
        Assert.That(ex.Message, Does.Contain(expectedName));
        Assert.That((await client.GetAsync<ProductId>()).Value, Is.EqualTo(9u));
    }

    private static DeviceCatalog<Dev> NullFactoryCatalog(bool viaDefault) => viaDefault
        ? new DeviceCatalog<Dev>().Default((c, id) => null!)
        : new DeviceCatalog<Dev>().Register("null maker", _ => true, (c, id) => null!);

    [Test]
    public async Task CancelDuringIdentification_ClosesClient()
    {
        await using var server = await ServerAsync(s => s.OnRequest<TargetName>(_ => ControlReply.Silent));
        var catalog = new DeviceCatalog<Dev>(clientOptions: new NetSdrControlClientOptions { ResponseTimeout = Limits.Test })
            .Register("any", _ => true, (c, id) => new Dev("any", c, id));
        using var cts = new CancellationTokenSource();
        var connect = catalog.ConnectAsync(At(server), cts.Token);
        await Eventually.ThatAsync(() => server.Received.Count > 0);
        cts.Cancel();
        await Assert.ThatAsync(() => connect.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        await Loopback.AssertServerFreeAsync(server);
    }

    [Test]
    public void NotRecognizedException_NamesTheDeviceAndTheCandidates()
    {
        var candidates = new[] { "Vega v2", "Vega v1" };
        var unnamed = new DeviceNotRecognizedException(new DeviceIdentityBuilder().Current, candidates);
        Assert.That(unnamed.Message, Is.EqualTo("Device was not recognized (name: ?, product id: ?); candidates: Vega v2, Vega v1."));

        var builder = new DeviceIdentityBuilder { Name = "NetSDR", ProductId = 0x03524453 };
        var named = new DeviceNotRecognizedException(builder.Current, candidates);
        Assert.That(
            named.Message,
            Is.EqualTo("Device was not recognized (name: NetSDR, product id: 03524453); candidates: Vega v2, Vega v1."));
        Assert.That(named.Candidates, Is.EqualTo(candidates));
        Assert.That(new DeviceNotRecognizedException(builder.Current, Array.Empty<string>()).Candidates, Is.Empty);
    }

    [Test]
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
        await Assert.ThatAsync(() => connect.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        await Loopback.AssertServerFreeAsync(server);
    }

    [Test]
    public async Task ParallelConnects_GiveIndependentDevices()
    {
        await using var first = await ServerAsync(s => s.Preload(new ProductId(1)));
        await using var second = await ServerAsync(s => s.Preload(new ProductId(2)));
        var catalog = new DeviceCatalog<Dev>().Register("any", _ => true, (c, id) => new Dev("any", c, id));
        var devices = await Task.WhenAll(catalog.ConnectAsync(At(first)), catalog.ConnectAsync(At(second))).WaitAsync(Limits.Test);
        await using var a = devices[0];
        await using var b = devices[1];
        Assert.That((a.Identity.ProductId!.Value, b.Identity.ProductId!.Value), Is.EqualTo((1u, 2u)));
        Assert.That(b.Client, Is.Not.SameAs(a.Client));
    }

    [Test]
    public async Task CatalogProbe_RunsAfterStandardProbes()
    {
        await using var server = await ServerAsync(s => s.Preload(new RfGain(0, -10)));
        var catalog = new DeviceCatalog<Dev>(new IdentificationOptions { Probes = { Probes.Item<RfGain, byte>(0) } })
            .Register("any", _ => true, (c, id) => new Dev("any", c, id));
        await using var device = await catalog.ConnectAsync(At(server));
        var codes = server.Received.Select(r => r.Code).ToArray();
        Assert.That(codes[^2..], Is.EqualTo(new ushort[] { 0x000A, 0x0038 }));
        Assert.That(device.Identity.Get<RfGain>().GainDb, Is.EqualTo(-10));
    }

    [Test]
    public async Task AttachAsync_HandsTheGivenClientToTheFactory()
    {
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            var forwarding = new ForwardingClient(client);
            var dev = await new DeviceCatalog<Dev>().Default((c, id) => new Dev("any", c, id)).AttachAsync(forwarding);
            Assert.That(dev.Client, Is.SameAs(forwarding));
        }
    }
}
