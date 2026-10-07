using System.Net;
using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaResilienceTests
{
    static ResilientControlClientOptions Fast() => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(150),
        LateReplyTimeout = TimeSpan.FromMilliseconds(600),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
    };

    static DeviceCatalog<VegaReceiverBase> Catalog() =>
        new DeviceCatalog<VegaReceiverBase>(new IdentificationOptions { Probes = { VegaProbes.Identify(VegaEmulator.DefaultKey) } })
            .Register("Vega v2",
                id => id.ProductId == VegaProtocol.ProductId && id.Get<VegaInfo>().Firmware >= new Version(2, 0),
                (c, id) => new VegaV2Receiver(c, id))
            .Register("Vega v1", id => id.ProductId == VegaProtocol.ProductId, (c, id) => new VegaV1Receiver(c, id));

    static Task<ResilientControlClient> ConnectAsync(VegaEmulator emulator, ResilientControlClientOptions options) =>
        ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), options).WaitAsync(Limits.Test);

    [Fact]
    public async Task Catalog_AttachAsync_OverResilientClient()
    {
        await using var emulator = new VegaEmulator();
        var product = ControlReply.Item(new ProductId(VegaProtocol.ProductId));
        int asked = 0;
        emulator.Server.OnRequest(ProductId.Code, _ => Interlocked.Increment(ref asked) == 1 ? product.After(TimeSpan.FromMilliseconds(250)) : product);
        emulator.Server.OnRequest(SerialNumber.Code, _ => ControlReply.Nak.After(TimeSpan.FromMilliseconds(250)));
        await emulator.StartAsync();
        var client = await ConnectAsync(emulator, Fast());
        VegaReceiverBase device = await Catalog().AttachAsync(client).WaitAsync(Limits.Test);
        Assert.IsType<VegaV2Receiver>(device);
        Assert.Contains(SerialNumber.Code, device.Identity.Unsupported);
        Assert.Equal(1, emulator.Server.Received.Count(r => r.Code == ProductId.Code));
        Assert.Equal(1, emulator.Server.Received.Count(r => r.Code == SerialNumber.Code));
        await device.DisposeAsync();                                           // the device owns the client
        Assert.True(client.Completion.IsCompletedSuccessfully);
    }
}
