using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using NetSdr.Control;
using NetSdr.Testing;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Tests.Control;

public class ControlClientLifecycleTests
{
    static NetSdrControlClientOptions Fast(bool fault = true) =>
        new() { ResponseTimeout = TimeSpan.FromMilliseconds(150), FaultOnTimeout = fault };

    // For tests where a request must time out and later ones must still be answered within the window.
    static NetSdrControlClientOptions Patient() =>
        new() { ResponseTimeout = TimeSpan.FromMilliseconds(400), FaultOnTimeout = false };

    const string ProductReply = "08 00 09 00 53 44 52 03";
    const string VersionReply = "06 00 03 00 11 02";

    [Fact]
    public async Task Timeout_WithFaultOnTimeout_FaultsClient()
    {
        await using var device = PipeDevice.Create(Fast());
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<TimeoutException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        Assert.False(device.Client.IsConnected);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => device.Client.GetAsync<ProductId>());
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Timeout_WithoutFault_KeepsConnection()
    {
        await using var device = PipeDevice.Create(Fast(fault: false));
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("06 00 03 00 11 02");
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
    }

    [Fact]
    public async Task Cancellation_ThrowsAndKeepsClient()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var call = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Limits.Test));
        Assert.True(device.Client.IsConnected);
    }

    [Fact]
    public async Task LateReplyOfAbandonedRequest_GoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Limits.Test));

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("08 00 09 00 53 44 52 03");   // late reply to the cancelled request
        await device.SendAsync("06 00 03 00 11 02");
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ushort)0x0009, late.Code);
    }

    [Fact]
    public async Task RemoteClose_FailsRequestAndCompletion()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        device.CloseRemote();
        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<IOException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
    }

    [Fact]
    public async Task Dispose_FailsPending_CompletesSuccessfully()
    {
        var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.Client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => call.WaitAsync(Limits.Test));
        await device.Client.Completion.WaitAsync(Limits.Test);
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        await device.Client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.Client.GetAsync<ProductId>());
        await device.DisposeAsync();
    }

    [Fact]
    public async Task BeforeConnect_Throws()
    {
        await using var client = new NetSdrControlClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<ProductId>());
    }

    // Timeouts

    [Fact]
    public async Task Timeout_MessageNamesTheRequest()
    {
        // The test's own 5 s limit also throws TimeoutException, so check the message comes from the client.
        await using var device = PipeDevice.Create(Fast());
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
        Assert.Contains("0x0009", ex.Message);
        Assert.Same(ex, await Assert.ThrowsAsync<TimeoutException>(() => device.Client.Completion.WaitAsync(Limits.Test)));
    }

    [Fact]
    public async Task Timeout_WithoutFault_LateReplyGoesToUnsolicited()
    {
        await using var device = PipeDevice.Create(Patient());
        var abandoned = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => abandoned.WaitAsync(Limits.Test));

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        await device.SendAsync(VersionReply);
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ushort)0x0009, late.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(-5000)]
    [InlineData(3_000_000_000)]
    public void Options_InvalidResponseTimeout_Throws(long milliseconds)
    {
        var options = new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetSdrControlClient(options));
    }

    [Fact]
    public async Task Options_InfiniteResponseTimeout_IsAccepted()
    {
        await using var device = PipeDevice.Create(new NetSdrControlClientOptions { ResponseTimeout = Timeout.InfiniteTimeSpan });
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.Equal(0x03524453u, (await call.WaitAsync(Limits.Test)).Value);
    }

    // Cancellation

    [Fact]
    public async Task Cancellation_WhileQueued_SendsNothing()
    {
        await using var device = PipeDevice.Create();
        var first = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        using var cts = new CancellationTokenSource();
        var queued = device.Client.SetAsync(new RfGain(0, -20), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Limits.Test));
        Assert.True(device.Client.IsConnected);

        await device.SendAsync(ProductReply);
        await first.WaitAsync(Limits.Test);
        var after = device.Client.GetAsync<InterfaceVersion>();
        Assert.Equal(Hex.Parse("04 20 03 00"), await device.ReadRequestAsync());   // the cancelled Set never reached the wire
        await device.SendAsync(VersionReply);
        Assert.Equal(529, (await after.WaitAsync(Limits.Test)).Version);
    }

    [Fact]
    public async Task Cancellation_BeforeCall_SendsNothing()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.Client.GetAsync<ProductId>(cts.Token));

        var next = device.Client.GetAsync<InterfaceVersion>();
        Assert.Equal(Hex.Parse("04 20 03 00"), await device.ReadRequestAsync());
        await device.SendAsync(VersionReply);
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
    }

    // The abandoned request

    [Fact]
    public async Task ReplyMatchingActiveRequest_AnswersIt_EvenWhenAbandonedPairIsTheSame()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Limits.Test));

        // The protocol cannot tell the cancelled request's late reply from this request's own, so the first one
        // answers it (the accepted cost); the wait for the old reply ends there.
        var next = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.Equal(0x03524453u, (await next.WaitAsync(Limits.Test)).Value);
        Assert.False(device.Client.Unsolicited.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DroppedReply_DoesNotBreakLaterRequestsForTheSameItem(bool byTimeout)
    {
        await using var device = PipeDevice.Create(Patient());
        using var cts = new CancellationTokenSource();
        var dropped = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        if (byTimeout)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => dropped.WaitAsync(Limits.Test));
        }
        else
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dropped.WaitAsync(Limits.Test));
        }

        // The device never answers the dropped request. Every later request for the same item is still answered.
        var second = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.Equal(0x03524453u, (await second.WaitAsync(Limits.Test)).Value);

        var third = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("08 00 09 00 53 44 52 04");
        Assert.Equal(0x04524453u, (await third.WaitAsync(Limits.Test)).Value);
        Assert.False(device.Client.Unsolicited.TryRead(out _));
    }

    [Fact]
    public async Task ReplyToActiveRequest_OfAnotherItem_KeepsAbandonedPair()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Limits.Test));

        var version = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync(VersionReply);
        Assert.Equal(529, (await version.WaitAsync(Limits.Test)).Version);

        // The cancelled ProductId request is still awaited, so its reply does not fail an unrelated request.
        var gain = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ushort)0x0009, late.Code);
        Assert.False(gain.IsCompleted);
        await device.SendAsync("06 00 48 00 00 0A");
        Assert.Equal(10, (await gain.WaitAsync(Limits.Test)).Level);
    }

    [Fact]
    public async Task Cancellation_LateReplyIsDeliveredOnlyOnce()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Limits.Test));
        await device.SendAsync(ProductReply);
        await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);

        // The abandoned pair was used up by the late reply, so the next request of the same item completes normally.
        var next = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("08 00 09 00 53 44 52 04");
        Assert.Equal(0x04524453u, (await next.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task Nak_WithoutActiveRequest_GoesToUnsolicitedAndClearsAbandonedPair()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Limits.Test));

        await device.SendAsync("02 00");
        var nak = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0), (nak.Type, nak.Code));
        Assert.True(nak.Payload.IsEmpty);

        var next = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        Assert.Equal(0x03524453u, (await next.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task Nak_WithActiveRequest_GoesToItAndKeepsAbandonedPair()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Limits.Test));

        var rejected = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("02 00");
        var ex = await Assert.ThrowsAsync<NetSdrNakException>(() => rejected.WaitAsync(Limits.Test));
        Assert.Equal(((ushort)0x0003, RequestType.Get), (ex.Code, ex.RequestType));
        Assert.False(device.Client.Unsolicited.TryRead(out _));

        // The pair is still abandoned: a ProductId reply is the late one and does not fail an unrelated request.
        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ushort)0x0009, late.Code);
        Assert.False(next.IsCompleted);
        await device.SendAsync(VersionReply);
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
    }

    [Fact]
    public async Task ForeignCodeReply_AbandonsRequest_LateRealReplyGoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        var failed = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync(VersionReply);
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => failed.WaitAsync(Limits.Test));

        var next = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(ProductReply);                        // the failed request's real reply
        var foreign = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ushort)0x0003, foreign.Code);
        Assert.Equal((ushort)0x0009, late.Code);
        Assert.False(next.IsCompleted);                              // it would have failed had the late reply not been recognised

        await device.SendAsync("06 00 48 00 00 0A");
        Assert.Equal(10, (await next.WaitAsync(Limits.Test)).Level);
    }

    [Fact]
    public async Task WrongReplyType_AbandonsRequest_LateRealReplyGoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        var failed = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("0A 40 20 00 00 90 C6 D5 00 00");     // RangeResponse to a Get
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => failed.WaitAsync(Limits.Test));

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("0A 00 20 00 00 90 C6 D5 00 00");     // the failed request's real reply
        await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0x0020), (late.Type, late.Code));
        Assert.False(next.IsCompleted);                              // it would have failed had the late reply not been recognised

        await device.SendAsync(VersionReply);
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
    }

    // Faults and disposal

    [Fact]
    public async Task Fault_CompletesUnsolicitedWithoutError_AfterCompletion()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("01 00");
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        // Completion is set before Unsolicited ends, so a consumer that sees the channel end sees the fault too.
        Assert.True(device.Client.Completion.IsFaulted);
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => device.Client.Completion);
    }

    [Fact]
    public async Task Dispose_AfterFault_KeepsCompletionFaulted()
    {
        var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("01 00");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));

        await device.Client.DisposeAsync();
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.Client.GetAsync<ProductId>());
        await device.DisposeAsync();
    }

    [Fact]
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
                Assert.True(wait.Elapsed < Limits.Test, $"The client did not fault (iteration {i}).");
                Thread.SpinWait(1);
            }

            await device.Client.DisposeAsync();
            Assert.True(device.Client.Completion.IsFaulted, $"Completion did not fault (iteration {i}).");
            await device.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_FailsQueuedCallers()
    {
        var device = PipeDevice.Create();
        var first = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        var queued = device.Client.GetAsync<InterfaceVersion>();
        await device.Client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(Limits.Test));
        await device.DisposeAsync();
    }

    [Fact]
    public async Task WriteFailure_FaultsClientWithIOException()
    {
        await using var client = new NetSdrControlClient();
        client.Attach(new Pipe().Reader, new FailingWriteStream(new IOException("The wire broke.")));
        var ex = await Assert.ThrowsAsync<IOException>(() => client.GetAsync<ProductId>());
        Assert.Equal("The wire broke.", ex.Message);
        Assert.Same(ex, await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test)));
        Assert.False(client.IsConnected);
        await client.Unsolicited.Completion.WaitAsync(Limits.Test);
    }

    [Fact]
    public async Task WriteFailure_OtherException_IsWrappedInIOException()
    {
        await using var client = new NetSdrControlClient();
        var cause = new NotSupportedException("Not writable.");
        client.Attach(new Pipe().Reader, new FailingWriteStream(cause));
        var ex = await Assert.ThrowsAsync<IOException>(() => client.GetAsync<ProductId>());
        Assert.Same(cause, ex.InnerException);
        await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
    }

    // Connecting

    [Fact]
    public async Task EndPoints_AreNull_BeforeConnect_AndForAPipeAttachedClient()
    {
        await using var unconnected = new NetSdrControlClient();
        Assert.Null(unconnected.LocalEndPoint);
        Assert.Null(unconnected.RemoteEndPoint);

        await using var device = PipeDevice.Create();
        Assert.Null(device.Client.LocalEndPoint);
        Assert.Null(device.Client.RemoteEndPoint);
    }

    [Fact]
    public async Task EndPoints_AfterConnect_AreTheTcpSocketEndPoints()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint!.Address);
        Assert.NotEqual(server.Port, client.LocalEndPoint.Port);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, server.Port), client.RemoteEndPoint);
    }

    [Fact]
    public async Task EndPoints_AfterConnectByHostName_AreIPv4()
    {
        // The socket for a host name may be dual-mode and report IPv4 addresses mapped to IPv6; the address the
        // application sends to the device in DataOutputUdpAddress.For has to be IPv4.
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint!.Address);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, server.Port), client.RemoteEndPoint);
    }

    [Fact]
    public async Task EndPoints_StayAvailable_AfterDispose()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server;
        var local = client.LocalEndPoint;
        await client.DisposeAsync();
        Assert.Equal(local, client.LocalEndPoint);
        Assert.Equal(server.Port, client.RemoteEndPoint!.Port);
    }

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_Throws()
    {
        await using var device = PipeDevice.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.Client.ConnectAsync("127.0.0.1"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.Client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 50000)));
        Assert.True(device.Client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_AfterDispose_Throws()
    {
        var client = new NetSdrControlClient();
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync("127.0.0.1"));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 50000)));
    }

    [Fact]
    public async Task ConnectAsync_AfterFault_Throws()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("01 00");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.Client.ConnectAsync("127.0.0.1"));
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
