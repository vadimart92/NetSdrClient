using System.Net;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientTimeoutTests
{
    [Test]
    public async Task CommandTimeout_DuringOutage()
    {
        var connector = new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam();
        options.CommandTimeout = TimeSpan.FromMilliseconds(300);
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            device.CloseRemote();
            await Eventually.ThatAsync(() => !client.IsConnected);
            var timeout = Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
            Assert.That(timeout.Message, Does.StartWith("Get of item 0x0003 did not complete within"));
            Assert.That(timeout.InnerException, Is.InstanceOf<IOException>());      // the cause of the loss, not null
        }
    }

    [Test]
    public async Task CommandTimeout_WhileQueuedBehindUnanswered()
    {
        var logs = new FakeLoggerFactory();
        var options = Resilient.Fast(logs);
        options.CommandTimeout = TimeSpan.FromMilliseconds(300);
        var (server, client) = await Resilient.StartAsync(options, s =>
        {
            s.OnRequest(ProductId.Code, _ => ControlReply.Item(new ProductId(7)).After(TimeSpan.FromMilliseconds(500)));
            s.Preload(new InterfaceVersion(529));
        });
        await using (server)
        await using (client)
        {
            using var cancelA = new CancellationTokenSource();
            var a = client.GetAsync<ProductId>(cancelA.Token);
            await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == ProductId.Code));
            cancelA.Cancel();                                                         // A's request stays on the line
            await Assert.ThatAsync(() => a.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
            await Assert.ThatAsync(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
            await Eventually.ThatAsync(() => logs.Events(1108).Count == 1);           // A's late reply settled the line
            Assert.That((await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
            Assert.That(server.Received.Count(r => r.Code == InterfaceVersion.Code), Is.EqualTo(1));
        }
    }

    [TestCase("before the call")]
    [TestCase("on admission")]
    [TestCase("on the line")]
    [TestCase("waiting for a reconnect")]
    public async Task Cancellation_BeforeWrite_NothingWritten(string where)
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s =>
        {
            s.Preload(new InterfaceVersion(529));
            s.OnRequest(ProductId.Code, _ => ControlReply.Item(new ProductId(7)).After(TimeSpan.FromMilliseconds(400)));
        });
        await using (server)
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            NetSdrControlClient? blocker = null;
            switch (where)
            {
                case "before the call":
                    cancel.Cancel();
                    break;
                case "on admission":                                                  // A holds admission until its late reply
                    _ = client.GetAsync<ProductId>();
                    await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == ProductId.Code));
                    break;
                case "on the line":                                                   // A was cancelled after its write and holds the line
                    using (var cancelA = new CancellationTokenSource())
                    {
                        var a = client.GetAsync<ProductId>(cancelA.Token);
                        await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == ProductId.Code));
                        cancelA.Cancel();
                        await Assert.ThatAsync(() => a.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
                    }

                    break;
                case "waiting for a reconnect":                                       // the server serves another client
                    blocker = new NetSdrControlClient();
                    await blocker.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)).WaitAsync(Limits.Test);
                    await server.DisconnectClientAsync().WaitAsync(Limits.Test);
                    await Eventually.ThatAsync(() => !client.IsConnected);
                    break;
            }

            var b = client.GetAsync<InterfaceVersion>(cancel.Token);
            if (!cancel.IsCancellationRequested)
            {
                await Task.Delay(100);
                cancel.Cancel();
            }

            await Assert.ThatAsync(() => b.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
            if (blocker is not null)
                await blocker.DisposeAsync().AsTask().WaitAsync(Limits.Test);
            await Task.Delay(600);                                                    // A's late reply or the reconnect has come
            Assert.That(server.Received.Select(r => r.Code), Does.Not.Contain(InterfaceVersion.Code));
        }
    }

    [Test]
    public async Task Cancellation_AfterWrite_ReturnsAtOnce()
    {
        var time = new FakeTimeProvider();
        var options = Resilient.Seam(time: time);
        options.ResponseTimeout = TimeSpan.FromSeconds(2);
        var (client, device) = await new PipeConnector(time).StartAsync(options);
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            var call = client.GetAsync<ProductId>(cancel.Token);
            await device.ReadRequestAsync();                                          // written, and never answered
            cancel.Cancel();
            // The client's clock stands still, so returning at all proves the call waited for nothing on it: neither the
            // response timeout nor the late reply.
            await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        }
    }

    [Test]
    public async Task Cancellation_NextSetOfSameItem_GetsItsOwnEcho()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs),
            s => s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Echo.After(TimeSpan.FromMilliseconds(300)))));
        await using (server)
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            var first = client.SetAsync(new AfGain(0, 1), cancel.Token);
            await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == AfGain.Code));
            cancel.Cancel();
            await Assert.ThatAsync(() => first.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
            Assert.That((await client.SetAsync(new AfGain(0, 2)).WaitAsync(Limits.Test)).Level, Is.EqualTo(2));
            await Eventually.ThatAsync(() => logs.Events(1108).Count == 1);
            Assert.That(
                (logs.Events(1108)[0].Value("Owner"), logs.Events(1108)[0].Value("Outcome")),
                Is.EqualTo(("CancelledCaller", "Reply")));
        }
    }

    [Test]
    public async Task Stress_CommandsHeartbeatsRandomDisconnects()
    {
        var logs = new FakeLoggerFactory();
        var random = new Random(20261007);
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs), s => s.OnRequest(AfGain.Code, request =>
        {
            double roll;
            lock (random)
                roll = random.NextDouble();
            if (roll < 0.1)
            {
                _ = s.DisconnectClientAsync();
                return ControlReply.Silent;
            }

            return roll < 0.2 ? ControlReply.Echo.After(TimeSpan.FromMilliseconds(200)) : ControlReply.Echo;   // ResponseTimeout + 50 ms
        }));
        await using (server)
        await using (client)
        {
            var callers = Enumerable.Range(0, 4).Select(caller => Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)                                          // 200 calls over 4 callers
                {
                    byte level = (byte)(caller * 50 + i);
                    var call = client.SetAsync(new AfGain(0, level));
                    Assert.That(await Task.WhenAny(call, Task.Delay(Limits.Test)), Is.SameAs(call));
                    try
                    {
                        Assert.That((await call).Level, Is.EqualTo(level));           // never somebody else's echo
                    }
                    catch (Exception e) when (e is IOException or TimeoutException)
                    {
                        // Four attempts were not enough across the drops; the spec allows it.
                    }
                }
            })).ToArray();
            await Task.WhenAll(callers);                                              // each call is capped at Limits.Test above
        }

        Assert.That(logs.Events(1106), Is.Empty);
    }
}
