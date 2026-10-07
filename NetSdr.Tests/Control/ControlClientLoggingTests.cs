using Microsoft.Extensions.Logging;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ControlClientLoggingTests
{
    const string Category = "NetSdr.Control.NetSdrControlClient";
    const string ProductReply = "08 00 09 00 53 44 52 03";

    static NetSdrControlClientOptions Logged(FakeLoggerFactory logs, bool fault = true, bool supervised = false) => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(150), FaultOnTimeout = fault, LoggerFactory = logs, Supervised = supervised,
    };

    [Fact]
    public async Task ControlClientLogging_LifecycleAndRequests()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        {
            await client.SetAsync(new RfGain(0, -20));
            await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>());
            await client.DisposeAsync();
        }

        var connected = Assert.Single(logs.Events(1000));
        Assert.Equal((LogLevel.Information, Category), (connected.Level, connected.Category));
        Assert.Equal($"Connected to {client.RemoteEndPoint} from {client.LocalEndPoint}", connected.Message);
        Assert.Equal((LogLevel.Debug, "RfGain"), (logs.Events(1003)[0].Level, logs.Events(1003)[0].Value("Item")));
        var reply = Assert.Single(logs.Events(1004));
        Assert.Equal("RfGain", reply.Value("Item"));
        Assert.True(reply.Span("Duration") >= TimeSpan.Zero);
        var nak = Assert.Single(logs.Events(1005));
        Assert.Equal((LogLevel.Debug, "ProductId"), (nak.Level, nak.Value("Item")));
        Assert.Equal(LogLevel.Information, Assert.Single(logs.Events(1001)).Level);
    }

    [Fact]
    public async Task ControlClientLogging_TimeoutAndForeignReply_Warning()
    {
        var logs = new FakeLoggerFactory();
        await using var silent = PipeDevice.Create(Logged(logs, fault: false));
        var timedOut = silent.Client.GetAsync<ProductId>();
        await silent.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut.WaitAsync(Limits.Test));
        Assert.Equal((LogLevel.Warning, "False"), (logs.Events(1006)[0].Level, logs.Events(1006)[0].Value("Faults")));

        await using var foreign = PipeDevice.Create(Logged(logs, fault: false));
        var failed = foreign.Client.GetAsync<InterfaceVersion>();
        await foreign.ReadRequestAsync();
        await foreign.SendAsync(ProductReply);                                     // a reply for another item
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => failed.WaitAsync(Limits.Test));
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Events(1007)).Level);
        Assert.Contains(logs.Events(1009), r => r.Value("Reason") == "Foreign");
    }

    [Fact]
    public async Task ControlClientLogging_DeviceClose_Error()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await server.DisconnectClientAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        }

        var faulted = Assert.Single(logs.Events(1002));
        Assert.Equal(LogLevel.Error, faulted.Level);
        Assert.IsAssignableFrom<IOException>(faulted.Exception);
        Assert.Empty(logs.Events(1001));
    }

    [Fact]
    public async Task ControlClientLogging_Supervised_DowngradesToDebug()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(ProductId.Code, _ => ControlReply.Silent),
            Logged(logs, fault: false, supervised: true));
        await using (server)
        await using (client)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<ProductId>());   // 1006
            await server.DisconnectClientAsync();                                             // 1002
            await Assert.ThrowsAnyAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        }

        var (other, closed) = await Loopback.StartAsync(options: Logged(logs, supervised: true));
        await using (other) await closed.DisposeAsync();                                      // 1001
        foreach (int id in new[] { 1000, 1001, 1002, 1006 })
        {
            Assert.NotEmpty(logs.Events(id));
            Assert.All(logs.Events(id), r => Assert.Equal(LogLevel.Debug, r.Level));
        }
    }

    [Theory]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Trace, true)]
    public async Task ControlClientLogging_TraceHex(LogLevel minimum, bool hex)
    {
        var logs = new FakeLoggerFactory(minimum);
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await client.SetAsync(new RfGain(0, -20));
        }

        Assert.Equal(hex, logs.Events(1010).Any(r => r.Message == "-> 0600380000EC"));
        Assert.Equal(hex, logs.Events(1011).Any(r => r.Message == "<- 0600380000EC"));
    }

    [Fact]
    public async Task ControlClientLogging_Unsolicited_Debug()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await server.SendUnsolicitedAsync(new AfGain(0, 9));
            await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
            await Assert.ThrowsAsync<NetSdrNakException>(
                () => client.SendAsync(RequestType.Get, 0x7FFF, ReadOnlyMemory<byte>.Empty));
        }

        var published = Assert.Single(logs.Events(1009));
        Assert.Equal((LogLevel.Debug, "Unsolicited"), (published.Level, published.Value("Reason")));
        Assert.Equal("raw", logs.Events(1003).Last().Value("Item"));
    }

    [Fact]
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
        await Assert.ThrowsAsync<TimeoutException>(() => late.WaitAsync(Limits.Test));
        await device.SendAsync(ProductReply);                  // the late reply of the abandoned request
        var foreign = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("05 00 01 00 41");              // Response 0x0001: foreign
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => foreign.WaitAsync(Limits.Test));
        Assert.Equal(new[] { "Unsolicited", "Data", "NoRequest", "Nak", "LateReply", "Foreign" },
            logs.Events(1009).Select(r => r.Value("Reason")));
    }

    // A provider that throws on the caller's side of a request changes nothing: the connect, the reply, the timeout and
    // the cancellation reach the caller as they would without logging, and the client stays consistent.
    [Theory]
    [InlineData(1000)]
    [InlineData(1003)]
    [InlineData(1010)]
    public async Task ControlClientLogging_ThrowingProvider_ConnectAndRequestUnaffected(int throwsAt)
    {
        var options = new NetSdrControlClientOptions { LoggerFactory = new ThrowingLoggerFactory(throwsAt) };
        var (server, client) = await Loopback.StartAsync(options: options);
        await using (server)
        await using (client)
        {
            Assert.True(client.IsConnected);
            Assert.Equal(-20, (await client.SetAsync(new RfGain(0, -20)).WaitAsync(Limits.Test)).GainDb);
            Assert.Equal(-10, (await client.SetAsync(new RfGain(0, -10)).WaitAsync(Limits.Test)).GainDb);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlClientLogging_ThrowingProvider_TimeoutStaysTimeout(bool fault)
    {
        var options = Logged(new FakeLoggerFactory(), fault);
        options.LoggerFactory = new ThrowingLoggerFactory(1006, 1002);
        await using var device = PipeDevice.Create(options);
        var timedOut = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut.WaitAsync(Limits.Test));
        if (fault)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => device.Client.Completion.WaitAsync(Limits.Test));
            return;
        }

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("06 00 03 00 11 02");
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
    }

    [Fact]
    public async Task ControlClientLogging_ThrowingProvider_CancellationStaysCancellation()
    {
        var options = Logged(new FakeLoggerFactory(), fault: false);
        options.LoggerFactory = new ThrowingLoggerFactory(1008);
        await using var device = PipeDevice.Create(options);
        using var cancel = new CancellationTokenSource();
        var cancelled = device.Client.GetAsync<ProductId>(cancel.Token);
        await device.ReadRequestAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Limits.Test));
        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("06 00 03 00 11 02");
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
    }

    // The resilient client awaits every inner disposal: one that threw would skip the rest of its cleanup.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlClientLogging_ThrowingProvider_DisposeCompletes(bool supervised)
    {
        var options = new NetSdrControlClientOptions { LoggerFactory = new ThrowingLoggerFactory(1001), Supervised = supervised };
        var (server, client) = await Loopback.StartAsync(options: options);
        await using (server)
        {
            await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
            Assert.True(client.Completion.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task ControlClientLogging_ThrowingProvider_DeviceCloseFaultsAndDisposes()
    {
        var options = new NetSdrControlClientOptions { LoggerFactory = new ThrowingLoggerFactory(1002) };
        var (server, client) = await Loopback.StartAsync(options: options);
        await using (server)
        {
            await server.DisconnectClientAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        }
    }

    /// <summary>Loggers enabled at every level that throw on the given event ids and drop every other entry.</summary>
    internal sealed class ThrowingLoggerFactory(params int[] throwsAt) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(throwsAt);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() { }

        private sealed class Logger(int[] throwsAt) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (throwsAt.Contains(eventId.Id)) throw new InvalidOperationException($"The logging provider failed at {eventId.Id}.");
            }
        }
    }

    [Fact]
    public async Task ControlClientLogging_HeaderOnlyFrameOfAnotherType_PublishedAsNak_WhileRequestInFlight()
    {
        var logs = new FakeLoggerFactory();
        await using var device = PipeDevice.Create(Logged(logs, fault: false));
        var call = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("02 20");                      // a header-only Unsolicited frame never answers a request
        await device.SendAsync("06 00 03 00 11 02");          // the real reply
        Assert.Equal(529, (await call.WaitAsync(Limits.Test)).Version);
        var published = Assert.Single(logs.Events(1009));
        Assert.Equal(("Unsolicited", "Nak"), (published.Value("ReplyType"), published.Value("Reason")));
    }
}
