using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaConnectTests
{
    [TestCase(VegaFirmware.V1, typeof(VegaV1Receiver), 1)]
    [TestCase(VegaFirmware.V2, typeof(VegaV2Receiver), 2)]
    public async Task Connect_PicksClientByFirmware(VegaFirmware firmware, Type receiverType, int major)
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync(firmware);
        await using var _ = emulator;
        await using var __ = vega;
        Assert.That(vega, Is.TypeOf(receiverType));
        Assert.That(vega.Identity.Get<VegaInfo>().Firmware, Is.EqualTo(new Version(major, 0)));
        Assert.That(emulator.IsUnlocked, Is.True);
        var codes = emulator.Server.Received.Select(r => r.Code).ToList();
        Assert.That(codes.IndexOf(0x0009) < codes.IndexOf(VegaProtocol.VendorUnlockCode), Is.True);
        Assert.That(codes.IndexOf(VegaProtocol.FirmwareInfoCode), Is.EqualTo(codes.IndexOf(VegaProtocol.VendorUnlockCode) + 1));
    }

    [Test]
    public async Task WrongKey_ThrowsNak_ClosesClient()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        var ex = await Assert.ThrowsAsync<NetSdrNakException>(() => VegaReceiverBase.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), 0x1234));
        Assert.That(ex.Code, Is.EqualTo(VegaProtocol.VendorUnlockCode));
        Assert.That(emulator.IsUnlocked, Is.False);
        Assert.That(emulator.Server.Received.Select(r => r.Code), Does.Not.Contain(VegaProtocol.FirmwareInfoCode));
        await VegaFixture.AssertServerFreeAsync(emulator.Server);
    }

    [Test]
    public async Task ForeignDevice_ThrowsVegaException_WithIdentity()
    {
        await using var server = new NetSdrTestServer();
        server.Preload(new ProductId(0x03524453));
        await server.StartAsync();
        var ex = await Assert.ThrowsAsync<VegaException>(() => VegaReceiverBase.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), VegaEmulator.DefaultKey));
        Assert.That(ex.Identity!.ProductId, Is.EqualTo(0x03524453u));
        Assert.That(
            server.Received.Select(r => r.Code),
            Does.Not.Contain(VegaProtocol.VendorUnlockCode).And.Not.Contain(VegaProtocol.FirmwareInfoCode));
        await VegaFixture.AssertServerFreeAsync(server);
    }

    [Test]
    public async Task VendorCommand_BeforeUnlock_IsNak()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var raw = new NetSdrControlClient();
        await raw.ConnectAsync("127.0.0.1", emulator.Port);
        await Assert.ThatAsync(() => raw.GetAsync<AntennaSelect, byte>(0), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task ConnectAsync_PassesLoggerFactoryToIdentification()
    {
        var logs = new CategoryRecorder();
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var vega = await VegaReceiverBase.ConnectAsync("127.0.0.1", emulator.Port, VegaEmulator.DefaultKey,
            new NetSdrControlClientOptions { LoggerFactory = logs });
        Assert.That(logs.Categories, Does.Contain("NetSdr.Identification.DeviceIdentity"));
        Assert.That(logs.Categories, Does.Contain("NetSdr.Identification.DeviceCatalog"));
    }

    private sealed class CategoryRecorder : ILoggerFactory
    {
        public ConcurrentBag<string> Categories { get; } = [];
        public ILogger CreateLogger(string categoryName) { Categories.Add(categoryName); return NullLogger.Instance; }
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
