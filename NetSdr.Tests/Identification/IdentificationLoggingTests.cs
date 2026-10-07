using System.Net;
using Microsoft.Extensions.Logging;
using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Identification;

public class IdentificationLoggingTests
{
    static IdentificationOptions Logged(FakeLoggerFactory logs, bool standard = true) =>
        new() { IncludeStandardProbes = standard, LoggerFactory = logs };

    static void PreloadAll(NetSdrTestServer s)
    {
        s.Preload(new TargetName("NetSDR"));
        s.Preload(new SerialNumber("PS000123"));
        s.Preload(new InterfaceVersion(529));
        for (byte id = 0; id <= 3; id++) s.Preload(new FirmwareVersion(id, (ushort)(100 + id)));
        s.Preload(new ProductId(0x12345678));
        s.Preload(new Options(0, 0, 0));
    }

    [Fact]
    public async Task IdentificationLogging_StandardProbes()
    {
        var bare = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            await DeviceIdentity.ReadAsync(client, Logged(bare));
        }

        Assert.Equal(9, bare.Events(1301).Count);
        Assert.Equal(4, bare.Events(1301).Count(r => r.Value("Item")!.StartsWith("FirmwareVersion id ")));
        Assert.Empty(bare.Events(1300));

        var full = new FakeLoggerFactory();
        var (server2, client2) = await Loopback.StartAsync(PreloadAll);
        await using (server2)
        await using (client2)
        {
            await DeviceIdentity.ReadAsync(client2, Logged(full));
        }

        Assert.Equal(9, full.Events(1300).Count);
        Assert.Contains(full.Events(1300), r => r.Message == "Standard probe TargetName 0x0001 answered: NetSDR");
        var read = Assert.Single(full.Events(1304));
        Assert.Equal((LogLevel.Information, "NetSdr.Identification.DeviceIdentity"), (read.Level, read.Category));
        Assert.Equal("none", read.Value("Unsupported"));
    }

    [Fact]
    public async Task IdentificationLogging_AppProbe()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            var options = Logged(logs, standard: false);
            options.Probes.Add(Probes.Item<InterfaceVersion>());
            options.Probes.Add(Probes.Item<ProductId>());   // NAK
            await DeviceIdentity.ReadAsync(client, options);
        }

        var probes = logs.Events(1302);
        Assert.Equal(("1", "2", "InterfaceVersion", "none"),
            (probes[0].Value("Index"), probes[0].Value("Count"), probes[0].Value("Facts"), probes[0].Value("Unsupported")));
        Assert.Equal(("none", "0x0009"), (probes[1].Value("Facts"), probes[1].Value("Unsupported")));
    }

    [Fact]
    public async Task IdentificationLogging_ProbeFails()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(TargetName.Code, _ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150) });
        await using (server)
        await using (client)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => DeviceIdentity.ReadAsync(client, Logged(logs)));
        }

        var failed = Assert.Single(logs.Events(1303));
        Assert.Equal(("TargetName", LogLevel.Debug), (failed.Value("Step"), failed.Level));
        Assert.IsType<TimeoutException>(failed.Exception);
    }

    [Fact]
    public async Task IdentificationLogging_ProbeValues_OptionsAndFirmwareWithoutVersion()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s =>
        {
            s.Preload(new Options(0x06, 0x01, 0x12345678));
            // Component 2 is answered without a version; the others are refused.
            s.OnRequest(FirmwareVersion.Code, r => r.Payload.Span[0] == 2 ? ControlReply.Bytes(new byte[] { 2 }) : ControlReply.Nak);
        });
        await using (server)
        await using (client)
        {
            var identity = await DeviceIdentity.ReadAsync(client, Logged(logs));
            Assert.DoesNotContain(FirmwareVersion.Code, identity.Unsupported);   // one component answered
        }

        var options = Assert.Single(logs.Events(1300), r => r.Value("Item") == "Options");
        Assert.Equal("flags 0x06, custom 0x01, detail 0x12345678", options.Value("Value"));
        var firmware = Assert.Single(logs.Events(1300), r => r.Value("Item")!.StartsWith("FirmwareVersion"));
        Assert.Equal(("FirmwareVersion id 2", "none"), (firmware.Value("Item"), firmware.Value("Value")));
        Assert.Equal(new[] { "FirmwareVersion id 0", "FirmwareVersion id 1", "FirmwareVersion id 3" },
            logs.Events(1301).Select(r => r.Value("Item")).Where(item => item!.StartsWith("FirmwareVersion")));
    }

    [Fact]
    public async Task IdentificationLogging_FailureStep_FirmwareComponentAndAppProbe()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(
            s => s.OnRequest(FirmwareVersion.Code, r => r.Payload.Span[0] == 1 ? ControlReply.Silent : ControlReply.Nak),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150) });
        await using (server)
        await using (client)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => DeviceIdentity.ReadAsync(client, Logged(logs)));
        }

        Assert.Equal("FirmwareVersion id 1", Assert.Single(logs.Events(1303)).Value("Step"));

        var probeLogs = new FakeLoggerFactory();
        var (server2, client2) = await Loopback.StartAsync();
        await using (server2)
        await using (client2)
        {
            var options = Logged(probeLogs, standard: false);
            options.Probes.Add((_, _, _) => Task.CompletedTask);
            options.Probes.Add((_, _, _) => throw new InvalidOperationException("probe"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceIdentity.ReadAsync(client2, options));
        }

        var failed = Assert.Single(probeLogs.Events(1303));
        Assert.Equal("probe 2 of 2", failed.Value("Step"));
        Assert.IsType<InvalidOperationException>(failed.Exception);
    }

    [Fact]
    public async Task CatalogLogging_Matched()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            await new DeviceCatalog<object>(Logged(logs, standard: false)).Register("Any", _ => true, (c, id) => id).AttachAsync(client);
            await new DeviceCatalog<object>(Logged(logs, standard: false)).Default((c, id) => id).AttachAsync(client);
        }

        var matched = logs.Events(1310);
        Assert.Equal(new[] { "Any", "default" }, matched.Select(r => r.Value("Registration")));
        Assert.All(matched, r => Assert.Equal(("NetSdr.Identification.DeviceCatalog", LogLevel.Information), (r.Category, r.Level)));
    }

    [Fact]
    public async Task CatalogLogging_NotRecognized()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            var catalog = new DeviceCatalog<object>(Logged(logs, standard: false))
                .Register("A", _ => false, (c, id) => id).Register("B", _ => false, (c, id) => id);
            await Assert.ThrowsAsync<DeviceNotRecognizedException>(() => catalog.AttachAsync(client));
        }

        var warning = Assert.Single(logs.Events(1311));
        Assert.Equal((LogLevel.Warning, "A, B"), (warning.Level, warning.Value("Candidates")));
        Assert.Empty(logs.Events(1312));
    }

    [Fact]
    public async Task CatalogLogging_FactoryThrows()
    {
        var logs = new FakeLoggerFactory();
        var catalog = new DeviceCatalog<object>(Logged(logs, standard: false))
            .Register("Broken", _ => true, (c, id) => throw new InvalidOperationException("boom"));
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        var endPoint = new IPEndPoint(IPAddress.Loopback, server.Port);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ConnectAsync(endPoint));
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(endPoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.AttachAsync(client));
        Assert.Equal(new[] { "the client is closed", "the caller keeps the client" },
            logs.Events(1312).Select(r => r.Value("ClientFate")));
        Assert.All(logs.Events(1312), r => Assert.IsType<InvalidOperationException>(r.Exception));
    }
}
