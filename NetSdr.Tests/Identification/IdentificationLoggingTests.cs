using System.Net;
using Microsoft.Extensions.Logging;
using NetSdr.Control;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Identification;

public class IdentificationLoggingTests
{
    private static IdentificationOptions Logged(FakeLoggerFactory logs, bool standard = true) =>
        new() { IncludeStandardProbes = standard, LoggerFactory = logs };

    private static void PreloadAll(NetSdrTestServer s)
    {
        s.Preload(new TargetName("NetSDR"));
        s.Preload(new SerialNumber("PS000123"));
        s.Preload(new InterfaceVersion(529));
        for (byte id = 0; id <= 3; id++)
            s.Preload(new FirmwareVersion(id, (ushort)(100 + id)));
        s.Preload(new ProductId(0x12345678));
        s.Preload(new Options(0, 0, 0));
    }

    [Test]
    public async Task IdentificationLogging_StandardProbes()
    {
        var bare = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            await DeviceIdentity.ReadAsync(client, Logged(bare));
        }

        Assert.That(bare.Events(1301), Has.Count.EqualTo(9));
        Assert.That(bare.Events(1301).Count(r => r.Value("Item")!.StartsWith("FirmwareVersion id ")), Is.EqualTo(4));
        Assert.That(bare.Events(1300), Is.Empty);

        var full = new FakeLoggerFactory();
        var (server2, client2) = await Loopback.StartAsync(PreloadAll);
        await using (server2)
        await using (client2)
        {
            await DeviceIdentity.ReadAsync(client2, Logged(full));
        }

        Assert.That(full.Events(1300), Has.Count.EqualTo(9));
        Assert.That(full.Events(1300).Select(r => r.Message), Does.Contain("Standard probe TargetName 0x0001 answered: NetSDR"));
        var read = full.Events(1304).Sole();
        Assert.That((read.Level, read.Category), Is.EqualTo((LogLevel.Information, "NetSdr.Identification.DeviceIdentity")));
        Assert.That(read.Value("Unsupported"), Is.EqualTo("none"));
    }

    [Test]
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
        Assert.That(
            (probes[0].Value("Index"), probes[0].Value("Count"), probes[0].Value("Facts"), probes[0].Value("Unsupported")),
            Is.EqualTo(("1", "2", "InterfaceVersion", "none")));
        Assert.That((probes[1].Value("Facts"), probes[1].Value("Unsupported")), Is.EqualTo(("none", "0x0009")));
    }

    [Test]
    public async Task IdentificationLogging_ProbeFails()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(TargetName.Code, _ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150) });
        await using (server)
        await using (client)
        {
            await Assert.ThatAsync(() => DeviceIdentity.ReadAsync(client, Logged(logs)), Throws.TypeOf<TimeoutException>());
        }

        var failed = logs.Events(1303).Sole();
        Assert.That((failed.Value("Step"), failed.Level), Is.EqualTo(("TargetName", LogLevel.Debug)));
        Assert.That(failed.Exception, Is.TypeOf<TimeoutException>());
    }

    [Test]
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
            Assert.That(identity.Unsupported, Does.Not.Contain(FirmwareVersion.Code));   // one component answered
        }

        var options = logs.Events(1300).Where(r => r.Value("Item") == "Options").Sole();
        Assert.That(options.Value("Value"), Is.EqualTo("flags 0x06, custom 0x01, detail 0x12345678"));
        var firmware = logs.Events(1300).Where(r => r.Value("Item")!.StartsWith("FirmwareVersion")).Sole();
        Assert.That((firmware.Value("Item"), firmware.Value("Value")), Is.EqualTo(("FirmwareVersion id 2", "none")));
        Assert.That(
            logs.Events(1301).Select(r => r.Value("Item")).Where(item => item!.StartsWith("FirmwareVersion")),
            Is.EqualTo(new[] { "FirmwareVersion id 0", "FirmwareVersion id 1", "FirmwareVersion id 3" }));
    }

    [Test]
    public async Task IdentificationLogging_FailureStep_FirmwareComponentAndAppProbe()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(
            s => s.OnRequest(FirmwareVersion.Code, r => r.Payload.Span[0] == 1 ? ControlReply.Silent : ControlReply.Nak),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150) });
        await using (server)
        await using (client)
        {
            await Assert.ThatAsync(() => DeviceIdentity.ReadAsync(client, Logged(logs)), Throws.TypeOf<TimeoutException>());
        }

        Assert.That(logs.Events(1303).Sole().Value("Step"), Is.EqualTo("FirmwareVersion id 1"));

        var probeLogs = new FakeLoggerFactory();
        var (server2, client2) = await Loopback.StartAsync();
        await using (server2)
        await using (client2)
        {
            var options = Logged(probeLogs, standard: false);
            options.Probes.Add((_, _, _) => Task.CompletedTask);
            options.Probes.Add((_, _, _) => throw new InvalidOperationException("probe"));
            await Assert.ThatAsync(() => DeviceIdentity.ReadAsync(client2, options), Throws.TypeOf<InvalidOperationException>());
        }

        var failed = probeLogs.Events(1303).Sole();
        Assert.That(failed.Value("Step"), Is.EqualTo("probe 2 of 2"));
        Assert.That(failed.Exception, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
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
        Assert.That(matched.Select(r => r.Value("Registration")), Is.EqualTo(new[] { "Any", "default" }));
        Assert.That(
            matched.Select(r => (r.Category, r.Level)),
            Has.All.EqualTo(("NetSdr.Identification.DeviceCatalog", LogLevel.Information)));
    }

    [Test]
    public async Task CatalogLogging_NotRecognized()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            var catalog = new DeviceCatalog<object>(Logged(logs, standard: false))
                .Register("A", _ => false, (c, id) => id).Register("B", _ => false, (c, id) => id);
            await Assert.ThatAsync(() => catalog.AttachAsync(client), Throws.TypeOf<DeviceNotRecognizedException>());
        }

        var warning = logs.Events(1311).Sole();
        Assert.That((warning.Level, warning.Value("Candidates")), Is.EqualTo((LogLevel.Warning, "A, B")));
        Assert.That(logs.Events(1312), Is.Empty);
    }

    [Test]
    public async Task CatalogLogging_FactoryThrows()
    {
        var logs = new FakeLoggerFactory();
        var catalog = new DeviceCatalog<object>(Logged(logs, standard: false))
            .Register("Broken", _ => true, (c, id) => throw new InvalidOperationException("boom"));
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        var endPoint = new IPEndPoint(IPAddress.Loopback, server.Port);
        await Assert.ThatAsync(() => catalog.ConnectAsync(endPoint), Throws.TypeOf<InvalidOperationException>());
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(endPoint);
        await Assert.ThatAsync(() => catalog.AttachAsync(client), Throws.TypeOf<InvalidOperationException>());
        Assert.That(
            logs.Events(1312).Select(r => r.Value("ClientFate")),
            Is.EqualTo(new[] { "the client is closed", "the caller keeps the client" }));
        Assert.That(logs.Events(1312).Select(r => r.Exception), Has.All.TypeOf<InvalidOperationException>());
    }
}
