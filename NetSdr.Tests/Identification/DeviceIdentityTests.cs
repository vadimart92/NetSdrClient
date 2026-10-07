using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;
using NetSdr.Tests.Control;

namespace NetSdr.Tests.Identification;

public class DeviceIdentityTests
{
    sealed record TestFact(int Value);
    sealed record OtherFact(string Value);

    [Fact]
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
        await using var _ = server; await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.Equal(("NetSDR", "MT123456", KnownModel.NetSdr), (id.Name, id.SerialNumber, id.Model));
        Assert.Equal(new Version(5, 29), id.InterfaceVersion);
        Assert.Equal(new Version(1, 4), id.BootVersion);
        Assert.Equal(new Version(5, 29), id.FirmwareVersion);
        Assert.Equal(new Version(3, 0), id.HardwareVersion);
        Assert.Equal(new FpgaInfo(3, 28), id.Fpga);
        Assert.Equal(0x03524453u, id.ProductId);
        Assert.Equal(Options.ReflockBoard, id.Options!.Value.Flags);
        Assert.Empty(id.Unsupported);
    }

    [Fact]
    public async Task BareDevice_AllNull_SixCodesUnsupported()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.Null(id.Name);
        Assert.Null(id.FirmwareVersion);
        Assert.Null(id.ProductId);
        Assert.Equal(KnownModel.Unknown, id.Model);
        Assert.Equal(new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004, 0x0009, 0x000A }, id.Unsupported.Order());
    }

    [Fact]
    public async Task FirmwareNakForOneId_LeavesOnlyThatFieldEmpty()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest<FirmwareVersion>(r =>
            r.Key<byte>() == 2 ? ControlReply.Nak : ControlReply.Item(new FirmwareVersion(r.Key<byte>(), 100))));
        await using var _ = server; await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.Null(id.HardwareVersion);
        Assert.Equal(new Version(1, 0), id.FirmwareVersion);
        Assert.DoesNotContain((ushort)0x0004, id.Unsupported);
    }

    [Fact]
    public async Task FirmwareEmptyPayload_IsAbsentNotUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(0x0004, r =>
            r.Payload.Span[0] == 0
                ? ControlReply.Bytes(ReadOnlyMemory<byte>.Empty)
                : ControlReply.Bytes(new byte[] { r.Payload.Span[0], 100, 0 })));
        await using var _ = server; await using var __ = client;
        var id = await DeviceIdentity.ReadAsync(client);
        Assert.Null(id.BootVersion);
        Assert.Equal(new Version(1, 0), id.FirmwareVersion);
        Assert.DoesNotContain((ushort)0x0004, id.Unsupported);
    }

    [Fact]
    public async Task ItemProbe_StoresFact_NakMarksUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new RfGain(0, -10)));
        await using var _ = server; await using var __ = client;
        var options = new IdentificationOptions
        {
            IncludeStandardProbes = false,
            Probes = { Probes.Item<RfGain, byte>(0), Probes.Item<AfGain, byte>(0) },
        };
        var id = await DeviceIdentity.ReadAsync(client, options);
        Assert.Equal(-10, id.Get<RfGain>().GainDb);
        Assert.False(id.TryGet<AfGain>(out AfGain absent));
        Assert.Equal(default, absent);
        Assert.Equal(new ushort[] { 0x0048 }, id.Unsupported);
        Assert.Equal(new ushort[] { 0x0038, 0x0048 }, server.Received.Select(r => r.Code));
    }

    [Fact]
    public async Task DelegateProbe_SeesStandardFields()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(0x41474556)));
        await using var _ = server; await using var __ = client;
        uint? seen = null;
        var options = new IdentificationOptions { Probes = { (c, b, ct) => { seen = b.Current.ProductId; return Task.CompletedTask; } } };
        await DeviceIdentity.ReadAsync(client, options);
        Assert.Equal(0x41474556u, seen);
    }

    [Fact]
    public async Task Facts_KeyedByType_LaterSetReplaces()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        var options = new IdentificationOptions
        {
            IncludeStandardProbes = false,
            Probes =
            {
                (c, b, ct) => { b.Set(new TestFact(1)); b.Set(new OtherFact("a")); b.Set(new TestFact(2)); return Task.CompletedTask; },
            },
        };
        var id = await DeviceIdentity.ReadAsync(client, options);
        Assert.Equal(2, id.Get<TestFact>().Value);
        Assert.Equal("a", id.Get<OtherFact>().Value);
        Assert.Equal(2, id.FactTypes.Count);
        var ex = Assert.Throws<KeyNotFoundException>(() => id.Get<string>());
        Assert.Contains("String", ex.Message);
    }

    [Fact]
    public async Task SilentDevice_TimeoutIsNotUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest<TargetName>(_ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<TimeoutException>(() => DeviceIdentity.ReadAsync(client));
    }

    [Fact]
    public async Task StandardProbes_AskInOrder_FirmwareOncePerComponent()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        await DeviceIdentity.ReadAsync(client);
        Assert.Equal(
            new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004, 0x0004, 0x0004, 0x0004, 0x0009, 0x000A },
            server.Received.Select(r => r.Code));
        Assert.Equal(
            new byte[] { 0, 1, 2, 3 },
            server.Received.Where(r => r.Code == 0x0004).Select(r => r.Payload.Span[0]));
    }

    [Fact]
    public async Task UnreadableReply_IsAnErrorNotUnsupported()
    {
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(0x0009, _ => ControlReply.Bytes(new byte[] { 1 })));
        await using var __ = server; await using var ___ = client;
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => DeviceIdentity.ReadAsync(client));
    }

    [Fact]
    public void Current_IsASnapshot_LaterChangesDoNotReachIt()
    {
        var builder = new DeviceIdentityBuilder();
        builder.Set(new TestFact(1));
        var first = builder.Current;

        builder.Set(new TestFact(2));
        builder.Set(new OtherFact("a"));
        builder.MarkUnsupported(0x0001);
        var second = builder.Current;

        Assert.Equal(1, first.Get<TestFact>().Value);
        Assert.Single(first.FactTypes);
        Assert.Empty(first.Unsupported);
        Assert.Equal(2, second.Get<TestFact>().Value);
        Assert.Equal(2, second.FactTypes.Count);
        Assert.Equal(new ushort[] { 0x0001 }, second.Unsupported);
    }

    [Fact]
    public async Task ReadAsync_LeavesClientOpen()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new ProductId(7)));
        await using var _ = server; await using var __ = client;
        await DeviceIdentity.ReadAsync(client);
        Assert.Equal(7u, (await client.GetAsync<ProductId>()).Value);
    }

    [Fact]
    public async Task ReadAsync_WorksThroughAnyINetSdrControlClient()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            var forwarding = new ForwardingClient(client);
            var identity = await DeviceIdentity.ReadAsync(forwarding);
            Assert.Equal(new Version(5, 29), identity.InterfaceVersion);
            Assert.Equal(9, forwarding.Requests);   // six standard items, 0x0004 four times
        }
    }
}
