using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Control;

namespace NetSdr.Tests.Identification;

public class DeviceIdentityTests
{
    private sealed record TestFact(int Value);
    private sealed record OtherFact(string Value);

    [Test]
    public async Task FullDevice_FillsEveryField()
    {
        var (server, client) = await Loopback.StartAsync(s =>
        {
            s.Preload(new TargetName("NetSDR"));
            s.Preload(new SerialNumber("MT123456"));
            s.Preload(new InterfaceVersion(529));
            s.Preload(new FirmwareVersion(0, 104));
            s.Preload(new FirmwareVersion(1, 529));
            s.Preload(new FirmwareVersion(2, 300));
            s.Preload(new FirmwareVersion(3, 0x1C03));
            s.Preload(new ProductId(0x03524453));
            s.Preload(new Options(Options.ReflockBoard, 0, 0));
        });
        await using var _ = server;
        await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.That((id.Name, id.SerialNumber, id.Model), Is.EqualTo(("NetSDR", "MT123456", KnownModel.NetSdr)));
        Assert.That(id.InterfaceVersion, Is.EqualTo(new Version(5, 29)));
        Assert.That(id.BootVersion, Is.EqualTo(new Version(1, 4)));
        Assert.That(id.FirmwareVersion, Is.EqualTo(new Version(5, 29)));
        Assert.That(id.HardwareVersion, Is.EqualTo(new Version(3, 0)));
        Assert.That(id.Fpga, Is.EqualTo(new FpgaInfo(3, 28)));
        Assert.That(id.ProductId, Is.EqualTo(0x03524453u));
        Assert.That(id.Options!.Value.Flags, Is.EqualTo(Options.ReflockBoard));
        Assert.That(id.Unsupported, Is.Empty);
    }

    [Test]
    public async Task BareDevice_AllNull_SixCodesUnsupported()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.That(id.Name, Is.Null);
        Assert.That(id.FirmwareVersion, Is.Null);
        Assert.That(id.ProductId, Is.Null);
        Assert.That(id.Model, Is.EqualTo(KnownModel.Unknown));
        Assert.That(id.Unsupported.Order(), Is.EqualTo(new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004, 0x0009, 0x000A }));
    }

    [Test]
    public async Task FirmwareNakForOneId_LeavesOnlyThatFieldEmpty()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest<FirmwareVersion>(r =>
            r.Key<byte>() == 2 ? ControlReply.Nak : ControlReply.Item(new FirmwareVersion(r.Key<byte>(), 100))));
        await using var _ = server;
        await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.That(id.HardwareVersion, Is.Null);
        Assert.That(id.FirmwareVersion, Is.EqualTo(new Version(1, 0)));
        Assert.That(id.Unsupported, Does.Not.Contain((ushort)0x0004));
    }

    [Test]
    public async Task FirmwareEmptyPayload_IsAbsentNotUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(0x0004, r =>
            r.Payload.Span[0] == 0
                ? ControlReply.Bytes(ReadOnlyMemory<byte>.Empty)
                : ControlReply.Bytes(new byte[] { r.Payload.Span[0], 100, 0 })));
        await using var _ = server;
        await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.That(id.BootVersion, Is.Null);
        Assert.That(id.FirmwareVersion, Is.EqualTo(new Version(1, 0)));
        Assert.That(id.Unsupported, Does.Not.Contain((ushort)0x0004));
    }

    [Test]
    public async Task ItemProbe_StoresFact_NakMarksUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new RfGain(0, -10)));
        await using var _ = server;
        await using var __ = client;
        var options = new IdentificationOptions
        {
            IncludeStandardProbes = false,
            Probes = { Probes.Item<RfGain, byte>(0), Probes.Item<AfGain, byte>(0) },
        };
        var id = await DeviceIdentity.ReadAsync(client, options);
        Assert.That(id.Get<RfGain>().GainDb, Is.EqualTo(-10));
        Assert.That(id.TryGet<AfGain>(out AfGain absent), Is.False);
        Assert.That(absent, Is.Default);
        Assert.That(id.Unsupported, Is.EqualTo(new ushort[] { 0x0048 }));
        Assert.That(server.Received.Select(r => r.Code), Is.EqualTo(new ushort[] { 0x0038, 0x0048 }));
    }

    [Test]
    public async Task DelegateProbe_SeesStandardFields()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(0x41474556)));
        await using var _ = server;
        await using var __ = client;
        uint? seen = null;
        var options = new IdentificationOptions { Probes = { (c, b, ct) => { seen = b.Current.ProductId; return Task.CompletedTask; } } };
        await DeviceIdentity.ReadAsync(client, options);
        Assert.That(seen, Is.EqualTo(0x41474556u));
    }

    [Test]
    public async Task Facts_KeyedByType_LaterSetReplaces()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        var options = new IdentificationOptions
        {
            IncludeStandardProbes = false,
            Probes =
            {
                (c, b, ct) => { b.Set(new TestFact(1)); b.Set(new OtherFact("a")); b.Set(new TestFact(2)); return Task.CompletedTask; },
            },
        };
        var id = await DeviceIdentity.ReadAsync(client, options);
        Assert.That(id.Get<TestFact>().Value, Is.EqualTo(2));
        Assert.That(id.Get<OtherFact>().Value, Is.EqualTo("a"));
        Assert.That(id.FactTypes, Has.Count.EqualTo(2));
        var ex = Assert.Throws<KeyNotFoundException>(() => id.Get<string>());
        Assert.That(ex.Message, Does.Contain("String"));
    }

    [Test]
    public async Task SilentDevice_TimeoutIsNotUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest<TargetName>(_ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = server;
        await using var __ = client;
        await Assert.ThatAsync(() => DeviceIdentity.ReadAsync(client), Throws.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task StandardProbes_AskInOrder_FirmwareOncePerComponent()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        await DeviceIdentity.ReadAsync(client);
        Assert.That(
            server.Received.Select(r => r.Code),
            Is.EqualTo(new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004, 0x0004, 0x0004, 0x0004, 0x0009, 0x000A }));
        Assert.That(server.Received.Where(r => r.Code == 0x0004).Select(r => r.Payload.Span[0]), Is.EqualTo(new byte[] { 0, 1, 2, 3 }));
    }

    [Test]
    public async Task UnreadableReply_IsAnErrorNotUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(0x0009, _ => ControlReply.Bytes(new byte[] { 1 })));
        await using var __ = server;
        await using var ___ = client;
        await Assert.ThatAsync(() => DeviceIdentity.ReadAsync(client), Throws.TypeOf<NetSdrProtocolException>());
    }

    [Test]
    public void Current_IsASnapshot_LaterChangesDoNotReachIt()
    {
        var builder = new DeviceIdentityBuilder();
        builder.Set(new TestFact(1));
        var first = builder.Current;

        builder.Set(new TestFact(2));
        builder.Set(new OtherFact("a"));
        builder.MarkUnsupported(0x0001);
        var second = builder.Current;

        Assert.That(first.Get<TestFact>().Value, Is.EqualTo(1));
        Assert.That(first.FactTypes, Has.Exactly(1).Items);
        Assert.That(first.Unsupported, Is.Empty);
        Assert.That(second.Get<TestFact>().Value, Is.EqualTo(2));
        Assert.That(second.FactTypes, Has.Count.EqualTo(2));
        Assert.That(second.Unsupported, Is.EqualTo(new ushort[] { 0x0001 }));
    }

    [Test]
    public async Task ReadAsync_LeavesClientOpen()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(7)));
        await using var _ = server;
        await using var __ = client;
        await DeviceIdentity.ReadAsync(client);
        Assert.That((await client.GetAsync<ProductId>()).Value, Is.EqualTo(7u));
    }

    [Test]
    public async Task ReadAsync_WorksThroughAnyINetSdrControlClient()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            var forwarding = new ForwardingClient(client);
            var identity = await DeviceIdentity.ReadAsync(forwarding);
            Assert.That(identity.InterfaceVersion, Is.EqualTo(new Version(5, 29)));
            Assert.That(forwarding.Requests, Is.EqualTo(9));   // six standard items, 0x0004 four times
        }
    }
}
