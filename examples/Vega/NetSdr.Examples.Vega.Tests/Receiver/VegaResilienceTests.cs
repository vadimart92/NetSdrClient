using System.Net;
using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Framing;
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

    [Fact]
    public async Task Vega_ReconnectDuringIdentification()
    {
        await using var emulator = new VegaEmulator();
        int asked = 0;
        emulator.Server.OnRequest<VegaFirmwareInfo>(request =>
        {
            if (Interlocked.Increment(ref asked) > 1) return ControlReply.Item(new VegaFirmwareInfo(200));
            _ = emulator.Server.DisconnectClientAsync();
            return ControlReply.Silent;
        });
        await emulator.StartAsync();
        var options = Fast();
        options.ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey), ct);  // always: session state
        var client = await ConnectAsync(emulator, options);
        await using var device = await Catalog().AttachAsync(client).WaitAsync(Limits.Test);
        Assert.IsType<VegaV2Receiver>(device);
        var codes = emulator.Server.Received.Where(r => r.Code != StatusCodes.Code).Select(r => r.Code).ToList();
        int firstInfo = codes.IndexOf(VegaProtocol.FirmwareInfoCode);
        int unlockAfterDrop = codes.IndexOf(VegaProtocol.VendorUnlockCode, firstInfo + 1);
        Assert.True(unlockAfterDrop > firstInfo);
        Assert.True(codes.IndexOf(VegaProtocol.FirmwareInfoCode, firstInfo + 1) > unlockAfterDrop);
    }

    [Fact]
    public async Task Vega_WrapperOverContextClient_StartsStream()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        DeviceIdentity? identity = null;
        bool streamed = false;
        var options = Fast();
        options.ConnectionRestored = async (ctx, ct) =>
        {
            await ctx.Client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey), ct);
            if (identity is not { } id) return;
            await using var wrapper = new VegaV2Receiver(ctx.Client, id);           // disposing it leaves the connection open
            await wrapper.StartStreamAsync(new IPEndPoint(IPAddress.Loopback, 50_999), 7_100_000, 200_000, ct);
            streamed = true;
        };
        var client = await ConnectAsync(emulator, options);
        await using var device = await Catalog().AttachAsync(client).WaitAsync(Limits.Test);
        identity = device.Identity;
        await emulator.Server.DisconnectClientAsync();
        await Eventually.ThatAsync(() => streamed && client.IsConnected);
        Assert.Contains(emulator.Server.Received, r => r.Code == ReceiverState.Code && r.Type == RequestType.Set);
        await device.GetLabelAsync().WaitAsync(Limits.Test);                        // the connection still serves the device
    }
}
