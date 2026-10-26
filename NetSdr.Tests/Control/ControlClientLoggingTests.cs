using Microsoft.Extensions.Logging;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ControlClientLoggingTests
{
    private const string Category = "NetSdr.Control.NetSdrControlClient";
    private const string ProductReply = "08 00 09 00 53 44 52 03";

    private static NetSdrControlClientOptions Logged(FakeLoggerFactory logs, bool fault = true, bool supervised = false) => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(150),
        FaultOnTimeout = fault,
        LoggerFactory = logs,
        Supervised = supervised,
    };

    [Test]
    public async Task ControlClientLogging_LifecycleAndRequests()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        {
            await client.SetAsync(new RfGain(0, -20));
            await Assert.ThatAsync(() => client.GetAsync<ProductId>(), Throws.TypeOf<NetSdrNakException>());
            await client.DisposeAsync();
        }

        var connected = logs.Events(1000).Sole();
        Assert.That((connected.Level, connected.Category), Is.EqualTo((LogLevel.Information, Category)));
        Assert.That(connected.Message, Is.EqualTo($"Connected to {client.RemoteEndPoint} from {client.LocalEndPoint}"));
        Assert.That((logs.Events(1003)[0].Level, logs.Events(1003)[0].Value("Item")), Is.EqualTo((LogLevel.Debug, "RfGain")));
        var reply = logs.Events(1004).Sole();
        Assert.That(reply.Value("Item"), Is.EqualTo("RfGain"));
        Assert.That(reply.Span("Duration") >= TimeSpan.Zero, Is.True);
        var nak = logs.Events(1005).Sole();
        Assert.That((nak.Level, nak.Value("Item")), Is.EqualTo((LogLevel.Debug, "ProductId")));
        Assert.That(logs.Events(1001).Sole().Level, Is.EqualTo(LogLevel.Information));
    }

    [Test]
    public async Task ControlClientLogging_TimeoutAndForeignReply_Warning()
    {
        var logs = new FakeLoggerFactory();
        await using var silent = PipeDevice.Create(Logged(logs, fault: false));
        var timedOut = silent.Client.GetAsync<ProductId>();
        await silent.ReadRequestAsync();
        await Assert.ThatAsync(() => timedOut.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        Assert.That((logs.Events(1006)[0].Level, logs.Events(1006)[0].Value("Faults")), Is.EqualTo((LogLevel.Warning, "False")));

        await using var foreign = PipeDevice.Create(Logged(logs, fault: false));
        var failed = foreign.Client.GetAsync<InterfaceVersion>();
        await foreign.ReadRequestAsync();
        await foreign.SendAsync(ProductReply);                                     // a reply for another item
        await Assert.ThatAsync(() => failed.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());
        Assert.That(logs.Events(1007).Sole().Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(logs.Events(1009).Select(r => r.Value("Reason")), Does.Contain("Foreign"));
    }

    [Test]
    public async Task ControlClientLogging_DeviceClose_Error()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await server.DisconnectClientAsync();
            await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.InstanceOf<IOException>());
        }

        var faulted = logs.Events(1002).Sole();
        Assert.That(faulted.Level, Is.EqualTo(LogLevel.Error));
        Assert.That(faulted.Exception, Is.InstanceOf<IOException>());
        Assert.That(logs.Events(1001), Is.Empty);
    }

    [Test]
    public async Task ControlClientLogging_Supervised_DowngradesToDebug()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(ProductId.Code, _ => ControlReply.Silent),
            Logged(logs, fault: false, supervised: true));
        await using (server)
        await using (client)
        {
            await Assert.ThatAsync(() => client.GetAsync<ProductId>(), Throws.TypeOf<TimeoutException>());   // 1006
            await server.DisconnectClientAsync();                                             // 1002
            await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.InstanceOf<IOException>());
        }

        var (other, closed) = await Loopback.StartAsync(options: Logged(logs, supervised: true));
        await using (other)
            await closed.DisposeAsync();                                      // 1001
        foreach (int id in new[] { 1000, 1001, 1002, 1006 })
        {
            Assert.That(logs.Events(id), Is.Not.Empty);
            Assert.That(logs.Events(id).Select(r => r.Level), Has.All.EqualTo(LogLevel.Debug));
        }
    }

    [TestCase(LogLevel.Debug, false)]
    [TestCase(LogLevel.Trace, true)]
    public async Task ControlClientLogging_TraceHex(LogLevel minimum, bool hex)
    {
        var logs = new FakeLoggerFactory(minimum);
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await client.SetAsync(new RfGain(0, -20));
        }

        Assert.That(logs.Events(1010).Any(r => r.Message == "-> 0600380000EC"), Is.EqualTo(hex));
        Assert.That(logs.Events(1011).Any(r => r.Message == "<- 0600380000EC"), Is.EqualTo(hex));
    }

    [Test]
    public async Task ControlClientLogging_Unsolicited_Debug()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await server.SendUnsolicitedAsync(new AfGain(0, 9));
            await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
            await Assert.ThatAsync(
                () => client.SendAsync(RequestType.Get, 0x7FFF, ReadOnlyMemory<byte>.Empty),
                Throws.TypeOf<NetSdrNakException>());
        }

        var published = logs.Events(1009).Sole();
        Assert.That((published.Level, published.Value("Reason")), Is.EqualTo((LogLevel.Debug, "Unsolicited")));
        Assert.That(logs.Events(1003).Last().Value("Item"), Is.EqualTo("raw"));
    }

    [Test]
    public async Task ControlClientLogging_PublishReasons()
    {
        var logs = new FakeLoggerFactory();
        await using var device = PipeDevice.Create(Logged(logs, fault: false));
        await device.SendAsync("06 20 48 00 00 09");          // Unsolicited AfGain
        await device.SendAsync("06 80 01 02 03 04");          // data item
        await device.SendAsync("06 00 03 00 11 02");          // a response nobody waits for
        await device.SendAsync("02 00");                      // a NAK without a request
        await Eventually.ThatAsync(() => logs.Events(1009).Count == 4);   // processed before a request is in flight
        var late = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThatAsync(() => late.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        await device.SendAsync(ProductReply);                  // the late reply of the abandoned request
        var foreign = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("05 00 01 00 41");              // Response 0x0001: foreign
        await Assert.ThatAsync(() => foreign.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());
        Assert.That(
            logs.Events(1009).Select(r => r.Value("Reason")),
            Is.EqualTo(new[] { "Unsolicited", "Data", "NoRequest", "Nak", "LateReply", "Foreign" }));
    }

    [Test]
    public async Task ControlClientLogging_HeaderOnlyFrameOfAnotherType_PublishedAsNak_WhileRequestInFlight()
    {
        var logs = new FakeLoggerFactory();
        await using var device = PipeDevice.Create(Logged(logs, fault: false));
        var call = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("02 20");                      // a header-only Unsolicited frame never answers a request
        await device.SendAsync("06 00 03 00 11 02");          // the real reply
        Assert.That((await call.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
        var published = logs.Events(1009).Sole();
        Assert.That((published.Value("ReplyType"), published.Value("Reason")), Is.EqualTo(("Unsolicited", "Nak")));
    }
}
