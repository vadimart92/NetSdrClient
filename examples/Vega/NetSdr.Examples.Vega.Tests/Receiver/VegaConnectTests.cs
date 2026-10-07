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
    [Theory]
    [InlineData(VegaFirmware.V1, typeof(VegaV1Receiver), 1)]
    [InlineData(VegaFirmware.V2, typeof(VegaV2Receiver), 2)]
    public async Task Connect_PicksClientByFirmware(VegaFirmware firmware, Type receiverType, int major)
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync(firmware);
        await using var _ = emulator; await using var __ = vega;
        Assert.IsType(receiverType, vega);
        Assert.Equal(new Version(major, 0), vega.Identity.Get<VegaInfo>().Firmware);
        Assert.True(emulator.IsUnlocked);
        var codes = emulator.Server.Received.Select(r => r.Code).ToList();
        Assert.True(codes.IndexOf(0x0009) < codes.IndexOf(VegaProtocol.VendorUnlockCode));
        Assert.Equal(codes.IndexOf(VegaProtocol.VendorUnlockCode) + 1, codes.IndexOf(VegaProtocol.FirmwareInfoCode));
    }

    [Fact]
    public async Task WrongKey_ThrowsNak_ClosesClient()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        var ex = await Assert.ThrowsAsync<NetSdrNakException>(
            () => VegaReceiverBase.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), 0x1234));
        Assert.Equal(VegaProtocol.VendorUnlockCode, ex.Code);
        Assert.False(emulator.IsUnlocked);
        Assert.DoesNotContain(emulator.Server.Received, r => r.Code == VegaProtocol.FirmwareInfoCode);
        await VegaFixture.AssertServerFreeAsync(emulator.Server);
    }

    [Fact]
    public async Task ForeignDevice_ThrowsVegaException_WithIdentity()
    {
        await using var server = new NetSdrTestServer();
        server.Preload(new ProductId(0x03524453));
        await server.StartAsync();
        var ex = await Assert.ThrowsAsync<VegaException>(
            () => VegaReceiverBase.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), VegaEmulator.DefaultKey));
        Assert.Equal(0x03524453u, ex.Identity!.ProductId);
        Assert.DoesNotContain(server.Received,
            r => r.Code is VegaProtocol.VendorUnlockCode or VegaProtocol.FirmwareInfoCode);
        await VegaFixture.AssertServerFreeAsync(server);
    }

    [Fact]
    public async Task VendorCommand_BeforeUnlock_IsNak()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var raw = new NetSdrControlClient();
        await raw.ConnectAsync("127.0.0.1", emulator.Port);
        await Assert.ThrowsAsync<NetSdrNakException>(() => raw.GetAsync<AntennaSelect, byte>(0));
    }

    [Fact]
    public async Task ConnectAsync_PassesLoggerFactoryToIdentification()
    {
        var logs = new CategoryRecorder();
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var vega = await VegaReceiverBase.ConnectAsync("127.0.0.1", emulator.Port, VegaEmulator.DefaultKey,
            new NetSdrControlClientOptions { LoggerFactory = logs });
        Assert.Contains("NetSdr.Identification.DeviceIdentity", logs.Categories);
        Assert.Contains("NetSdr.Identification.DeviceCatalog", logs.Categories);
    }

    sealed class CategoryRecorder : ILoggerFactory
    {
        public ConcurrentBag<string> Categories { get; } = [];
        public ILogger CreateLogger(string categoryName) { Categories.Add(categoryName); return NullLogger.Instance; }
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
