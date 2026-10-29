using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ControlClientLifecycleTests
{
    private static NetSdrControlClientOptions Fast(bool fault = true) =>
        new() { ResponseTimeout = TimeSpan.FromMilliseconds(150), FaultOnTimeout = fault };

    // For tests where a request must time out and later ones must still be answered within the window.
    private static NetSdrControlClientOptions Patient() =>
        new() { ResponseTimeout = TimeSpan.FromMilliseconds(400), FaultOnTimeout = false };

    private const string ProductReply = "08 00 09 00 53 44 52 03";
    private const string VersionReply = "06 00 03 00 11 02";

    [Test]
    public async Task Timeout_WithFaultOnTimeout_FaultsClient()
    {
        await using var device = PipeDevice.Create(Fast());
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        await Assert.ThatAsync(() => device.Client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        Assert.That(device.Client.IsConnected, Is.False);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => device.Client.GetAsync<ProductId>());
        Assert.That(ex.InnerException, Is.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task Timeout_WithoutFault_KeepsConnection()
    {
        await using var device = PipeDevice.Create(Fast(fault: false));
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("06 00 03 00 11 02");
        Assert.That((await next.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
    }

    [Test]
    public async Task Cancellation_ThrowsAndKeepsClient()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var call = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(device.Client.IsConnected, Is.True);
    }

    [Test]
    public async Task LateReplyOfAbandonedRequest_GoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => abandoned.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("08 00 09 00 53 44 52 03");   // late reply to the cancelled request
        await device.SendAsync("06 00 03 00 11 02");
        Assert.That((await next.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(late.Code, Is.EqualTo((ushort)0x0009));
    }

    [Test]
    public async Task RemoteClose_FailsRequestAndCompletion()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        device.CloseRemote();
        await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        await Assert.ThatAsync(() => device.Client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
    }

    [Test]
    public async Task Dispose_FailsPending_CompletesSuccessfully()
    {
        var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.Client.DisposeAsync();
        await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.TypeOf<ObjectDisposedException>());
        await device.Client.Completion.WaitAsync(Limits.Test);
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        await device.Client.DisposeAsync();
        await Assert.ThatAsync(() => device.Client.GetAsync<ProductId>(), Throws.TypeOf<ObjectDisposedException>());
        await device.DisposeAsync();
    }

    [Test]
    public async Task BeforeConnect_Throws()
    {
        await using var client = new NetSdrControlClient();
        await Assert.ThatAsync(() => client.GetAsync<ProductId>(), Throws.TypeOf<InvalidOperationException>());
    }

    // Timeouts

    [Test]
    public async Task Timeout_MessageNamesTheRequest()
    {
        // The test's own 5 s limit also throws TimeoutException, so check the message comes from the client.
        await using var device = PipeDevice.Create(Fast());
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
        Assert.That(ex.Message, Does.Contain("0x0009"));
        Assert.That(await Assert.ThrowsAsync<TimeoutException>(() => device.Client.Completion.WaitAsync(Limits.Test)), Is.SameAs(ex));
    }

    [Test]
    public async Task Timeout_WithoutFault_LateReplyGoesToUnsolicited()
    {
        await using var device = PipeDevice.Create(Patient());
        var abandoned = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThatAsync(() => abandoned.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        await device.SendAsync(VersionReply);
        Assert.That((await next.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(late.Code, Is.EqualTo((ushort)0x0009));
    }

    [TestCase(0)]
    [TestCase(-2)]
    [TestCase(-5000)]
    [TestCase(3_000_000_000)]
    public void Options_InvalidResponseTimeout_Throws(long milliseconds)
    {
        var options = new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.That(() => new NetSdrControlClient(options), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task Options_InfiniteResponseTimeout_IsAccepted()
    {
        await using var device = PipeDevice.Create(new NetSdrControlClientOptions { ResponseTimeout = Timeout.InfiniteTimeSpan });
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.That((await call.WaitAsync(Limits.Test)).Value, Is.EqualTo(0x03524453u));
    }

    // Cancellation

    [Test]
    public async Task Cancellation_WhileQueued_SendsNothing()
    {
        await using var device = PipeDevice.Create();
        var first = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        using var cts = new CancellationTokenSource();
        var queued = device.Client.SetAsync(new RfGain(0, -20), cts.Token);
        await cts.CancelAsync();
        await Assert.ThatAsync(() => queued.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(device.Client.IsConnected, Is.True);

        await device.SendAsync(ProductReply);
        await first.WaitAsync(Limits.Test);
        var after = device.Client.GetAsync<InterfaceVersion>();
        Assert.That(await device.ReadRequestAsync(), Is.EqualTo(Hex.Parse("04 20 03 00")));   // the cancelled Set never reached the wire
        await device.SendAsync(VersionReply);
        Assert.That((await after.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
    }

    [Test]
    public async Task Cancellation_BeforeCall_SendsNothing()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => device.Client.GetAsync<ProductId>(cts.Token), Throws.InstanceOf<OperationCanceledException>());

        var next = device.Client.GetAsync<InterfaceVersion>();
        Assert.That(await device.ReadRequestAsync(), Is.EqualTo(Hex.Parse("04 20 03 00")));
        await device.SendAsync(VersionReply);
        Assert.That((await next.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
    }

    // The abandoned request

    [Test]
    public async Task ReplyMatchingActiveRequest_AnswersIt_EvenWhenAbandonedPairIsTheSame()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => abandoned.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());

        // The protocol cannot tell the cancelled request's late reply from this request's own, so the first one
        // answers it (the accepted cost); the wait for the old reply ends there.
        var next = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.That((await next.WaitAsync(Limits.Test)).Value, Is.EqualTo(0x03524453u));
        Assert.That(device.Client.Unsolicited.TryRead(out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DroppedReply_DoesNotBreakLaterRequestsForTheSameItem(bool byTimeout)
    {
        await using var device = PipeDevice.Create(Patient());
        using var cts = new CancellationTokenSource();
        var dropped = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        if (byTimeout)
        {
            await Assert.ThatAsync(() => dropped.WaitAsync(Limits.Test), Throws.TypeOf<TimeoutException>());
        }
        else
        {
            await cts.CancelAsync();
            await Assert.ThatAsync(() => dropped.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        }

        // The device never answers the dropped request. Every later request for the same item is still answered.
        var second = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.That((await second.WaitAsync(Limits.Test)).Value, Is.EqualTo(0x03524453u));

        var third = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("08 00 09 00 53 44 52 04");
        Assert.That((await third.WaitAsync(Limits.Test)).Value, Is.EqualTo(0x04524453u));
        Assert.That(device.Client.Unsolicited.TryRead(out _), Is.False);
    }

    [Test]
    public async Task ReplyToActiveRequest_OfAnotherItem_KeepsAbandonedPair()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => abandoned.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());

        var version = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync(VersionReply);
        Assert.That((await version.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));

        // The cancelled ProductId request is still awaited, so its reply does not fail an unrelated request.
        var gain = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(late.Code, Is.EqualTo((ushort)0x0009));
        Assert.That(gain.IsCompleted, Is.False);
        await device.SendAsync("06 00 48 00 00 0A");
        Assert.That((await gain.WaitAsync(Limits.Test)).Level, Is.EqualTo(10));
    }

    [Test]
    public async Task Cancellation_LateReplyIsDeliveredOnlyOnce()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => abandoned.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());
        await device.SendAsync(ProductReply);
        await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);

        // The abandoned pair was used up by the late reply, so the next request of the same item completes normally.
        var next = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("08 00 09 00 53 44 52 04");
        Assert.That((await next.WaitAsync(Limits.Test)).Value, Is.EqualTo(0x04524453u));
    }

    [Test]
    public async Task Nak_WithoutActiveRequest_GoesToUnsolicitedAndClearsAbandonedPair()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => abandoned.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());

        await device.SendAsync("02 00");
        var nak = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That((nak.Type, nak.Code), Is.EqualTo((ReplyType.Response, (ushort)0)));
        Assert.That(nak.Payload.IsEmpty, Is.True);

        var next = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.That((await next.WaitAsync(Limits.Test)).Value, Is.EqualTo(0x03524453u));
    }

    [Test]
    public async Task Nak_WithActiveRequest_GoesToItAndKeepsAbandonedPair()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        await cts.CancelAsync();
        await Assert.ThatAsync(() => abandoned.WaitAsync(Limits.Test), Throws.InstanceOf<OperationCanceledException>());

        var rejected = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("02 00");
        var ex = await Assert.ThrowsAsync<NetSdrNakException>(() => rejected.WaitAsync(Limits.Test));
        Assert.That((ex.Code, ex.RequestType), Is.EqualTo(((ushort)0x0003, RequestType.Get)));
        Assert.That(device.Client.Unsolicited.TryRead(out _), Is.False);

        // The pair is still abandoned: a ProductId reply is the late one and does not fail an unrelated request.
        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(late.Code, Is.EqualTo((ushort)0x0009));
        Assert.That(next.IsCompleted, Is.False);
        await device.SendAsync(VersionReply);
        Assert.That((await next.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
    }

    [Test]
    public async Task ForeignCodeReply_AbandonsRequest_LateRealReplyGoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        var failed = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(VersionReply);
        await Assert.ThatAsync(() => failed.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());

        var next = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);                        // the failed request's real reply
        var foreign = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(foreign.Code, Is.EqualTo((ushort)0x0003));
        Assert.That(late.Code, Is.EqualTo((ushort)0x0009));
        Assert.That(next.IsCompleted, Is.False);                     // it would have failed had the late reply not been recognised

        await device.SendAsync("06 00 48 00 00 0A");
        Assert.That((await next.WaitAsync(Limits.Test)).Level, Is.EqualTo(10));
    }

    [Test]
    public async Task WrongReplyType_AbandonsRequest_LateRealReplyGoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        var failed = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("0A 40 20 00 00 90 C6 D5 00 00");     // RangeResponse to a Get
        await Assert.ThatAsync(() => failed.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("0A 00 20 00 00 90 C6 D5 00 00");     // the failed request's real reply
        await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That((late.Type, late.Code), Is.EqualTo((ReplyType.Response, (ushort)0x0020)));
        Assert.That(next.IsCompleted, Is.False);                     // it would have failed had the late reply not been recognised

        await device.SendAsync(VersionReply);
        Assert.That((await next.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
    }

    // Faults and disposal

    [Test]
    public async Task Fault_CompletesUnsolicitedWithoutError_AfterCompletion()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("01 00");
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        // Completion is set before Unsolicited ends, so a consumer that sees the channel end sees the fault too.
        Assert.That(device.Client.Completion.IsFaulted, Is.True);
        await Assert.ThatAsync(() => device.Client.Completion, Throws.TypeOf<NetSdrProtocolException>());
    }

    [Test]
    public async Task Dispose_AfterFault_KeepsCompletionFaulted()
    {
        var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("01 00");
        await Assert.ThatAsync(() => call.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());

        await device.Client.DisposeAsync();
        await Assert.ThatAsync(() => device.Client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        await Assert.ThatAsync(() => device.Client.GetAsync<ProductId>(), Throws.TypeOf<ObjectDisposedException>());
        await device.DisposeAsync();
    }

    [Test]
    public async Task Dispose_RacingAFault_KeepsCompletionFaulted()
    {
        for (int i = 0; i < 200; i++)
        {
            var device = PipeDevice.Create();
            await device.SendAsync("01 00");
            // Dispose as soon as the fault is visible, possibly before the fault has finished shutting down.
            var wait = Stopwatch.StartNew();
            while (device.Client.IsConnected)
            {
                Assert.That(wait.Elapsed < Limits.Test, Is.True, $"The client did not fault (iteration {i}).");
                Thread.SpinWait(1);
            }

            await device.Client.DisposeAsync();
            Assert.That(device.Client.Completion.IsFaulted, Is.True, $"Completion did not fault (iteration {i}).");
            await device.DisposeAsync();
        }
    }

    [Test]
    public async Task Dispose_FailsQueuedCallers()
    {
        var device = PipeDevice.Create();
        var first = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        var queued = device.Client.GetAsync<InterfaceVersion>();
        await device.Client.DisposeAsync();
        await Assert.ThatAsync(() => first.WaitAsync(Limits.Test), Throws.TypeOf<ObjectDisposedException>());
        await Assert.ThatAsync(() => queued.WaitAsync(Limits.Test), Throws.TypeOf<ObjectDisposedException>());
        await device.DisposeAsync();
    }

    [Test]
    public async Task WriteFailure_FaultsClientWithIOException()
    {
        await using var client = new NetSdrControlClient();
        client.Attach(new Pipe().Reader, new FailingWriteStream(new IOException("The wire broke.")));
        var ex = await Assert.ThrowsAsync<IOException>(() => client.GetAsync<ProductId>());
        Assert.That(ex.Message, Is.EqualTo("The wire broke."));
        Assert.That(await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test)), Is.SameAs(ex));
        Assert.That(client.IsConnected, Is.False);
        await client.Unsolicited.Completion.WaitAsync(Limits.Test);
    }

    [Test]
    public async Task WriteFailure_OtherException_IsWrappedInIOException()
    {
        await using var client = new NetSdrControlClient();
        var cause = new NotSupportedException("Not writable.");
        client.Attach(new Pipe().Reader, new FailingWriteStream(cause));
        var ex = await Assert.ThrowsAsync<IOException>(() => client.GetAsync<ProductId>());
        Assert.That(ex.InnerException, Is.SameAs(cause));
        await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
    }

    // Connecting

    [Test]
    public async Task EndPoints_AreNull_BeforeConnect_AndForAPipeAttachedClient()
    {
        await using var unconnected = new NetSdrControlClient();
        Assert.That(unconnected.LocalEndPoint, Is.Null);
        Assert.That(unconnected.RemoteEndPoint, Is.Null);

        await using var device = PipeDevice.Create();
        Assert.That(device.Client.LocalEndPoint, Is.Null);
        Assert.That(device.Client.RemoteEndPoint, Is.Null);
    }

    [Test]
    public async Task EndPoints_AfterConnect_AreTheTcpSocketEndPoints()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        await using var __ = client;
        Assert.That(client.LocalEndPoint!.Address, Is.EqualTo(IPAddress.Loopback));
        Assert.That(client.LocalEndPoint.Port, Is.Not.EqualTo(server.Port));
        Assert.That(client.RemoteEndPoint, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, server.Port)));
    }

    [Test]
    public async Task EndPoints_AfterConnectByHostName_AreIPv4()
    {
        // The socket for a host name may be dual-mode and report IPv4 addresses mapped to IPv6; the address the
        // application sends to the device in DataOutputUdpAddress.For has to be IPv4.
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        Assert.That(client.LocalEndPoint!.Address, Is.EqualTo(IPAddress.Loopback));
        Assert.That(client.RemoteEndPoint, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, server.Port)));
    }

    [Test]
    public async Task EndPoints_StayAvailable_AfterDispose()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        var local = client.LocalEndPoint;
        await client.DisposeAsync();
        Assert.That(client.LocalEndPoint, Is.EqualTo(local));
        Assert.That(client.RemoteEndPoint!.Port, Is.EqualTo(server.Port));
    }

    // Keepalive

    [Test]
    public void Options_KeepAlive_DefaultsToTenSeconds() =>
        Assert.That(new NetSdrControlClientOptions().KeepAlive, Is.EqualTo(TimeSpan.FromSeconds(10)));

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(999)]
    [TestCase(3_000_000_000)]
    public void Options_InvalidKeepAlive_Throws(long milliseconds)
    {
        var options = new NetSdrControlClientOptions { KeepAlive = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.That(() => new NetSdrControlClient(options), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task KeepAlive_IsSetOnTheSocket_InWholeSecondsRoundedUp()
    {
        var (server, client) = await Loopback.StartAsync(
            options: new NetSdrControlClientOptions { KeepAlive = TimeSpan.FromSeconds(7.2) });
        await using var _ = server;
        await using var __ = client;
        AssertKeepAlive(client.Socket!, seconds: 8);
    }

    [Test]
    public async Task KeepAlive_IsSetOnTheSocket_OfAConnectByHostName()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        AssertKeepAlive(client.Socket!, seconds: 10);
    }

    [Test]
    public async Task KeepAlive_Null_LeavesItOff()
    {
        var (server, client) = await Loopback.StartAsync(options: new NetSdrControlClientOptions { KeepAlive = null });
        await using var _ = server;
        await using var __ = client;
        Assert.That((int)client.Socket!.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!, Is.EqualTo(0));
    }

    private static void AssertKeepAlive(Socket socket, int seconds)
    {
        Assert.That((int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!, Is.Not.EqualTo(0));
        Assert.That((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!, Is.EqualTo(seconds));
        Assert.That((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!, Is.EqualTo(2));
        Assert.That((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!, Is.EqualTo(3));
    }

    [Test]
    public async Task ConnectAsync_WhenAlreadyConnected_Throws()
    {
        await using var device = PipeDevice.Create();
        await Assert.ThatAsync(() => device.Client.ConnectAsync("127.0.0.1"), Throws.TypeOf<InvalidOperationException>());
        await Assert.ThatAsync(
            () => device.Client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 50000)),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(device.Client.IsConnected, Is.True);
    }

    [Test]
    public async Task ConnectAsync_AfterDispose_Throws()
    {
        var client = new NetSdrControlClient();
        await client.DisposeAsync();
        await Assert.ThatAsync(() => client.ConnectAsync("127.0.0.1"), Throws.TypeOf<ObjectDisposedException>());
        await Assert.ThatAsync(
            () => client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 50000)),
            Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public async Task ConnectAsync_AfterFault_Throws()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("01 00");
        await Assert.ThatAsync(() => device.Client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());
        await Assert.ThatAsync(() => device.Client.ConnectAsync("127.0.0.1"), Throws.TypeOf<InvalidOperationException>());
    }

    /// <summary>An output stream whose writes always fail.</summary>
    private sealed class FailingWriteStream(Exception error) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(error);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw error;
    }
}
