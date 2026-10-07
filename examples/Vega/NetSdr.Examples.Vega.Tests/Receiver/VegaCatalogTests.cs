using System.Net;
using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaCatalogTests
{
    sealed record GenericDevice(INetSdrControlClient Client, DeviceIdentity Identity) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    static DeviceCatalog<IAsyncDisposable> Catalog() =>
        new DeviceCatalog<IAsyncDisposable>(new IdentificationOptions { Probes = { VegaProbes.Identify(VegaEmulator.DefaultKey) } })
            .Register("Vega v2",
                id => id.ProductId == VegaProtocol.ProductId && id.Get<VegaInfo>().Firmware >= new Version(2, 0),
                (c, id) => new VegaV2Receiver(c, id))
            .Register("Vega v1", id => id.ProductId == VegaProtocol.ProductId, (c, id) => new VegaV1Receiver(c, id))
            .Default((c, id) => new GenericDevice(c, id));

    [Fact]
    public async Task OwnCatalog_VegaGetsVersionClient_OtherGetsDefault()
    {
        await using var emulator = new VegaEmulator(firmware: VegaFirmware.V1);
        await emulator.StartAsync();
        await using var bare = new NetSdrTestServer();
        await bare.StartAsync();
        var catalog = Catalog();
        await using var vega = await catalog.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port));
        await using var other = await catalog.ConnectAsync(new IPEndPoint(IPAddress.Loopback, bare.Port));
        Assert.IsType<VegaV1Receiver>(vega);
        Assert.IsType<GenericDevice>(other);
    }
}
