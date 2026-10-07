using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientRestoreTests
{
    private static ResilientControlClientOptions Restoring(FakeLoggerFactory logs, Func<ConnectionRestoredContext, CancellationToken, Task> callback)
    {
        var options = Resilient.Fast(logs);
        options.ConnectionRestored = callback;
        return options;
    }

    private static async Task DropAndRestoreAsync(NetSdrTestServer server, FakeLoggerFactory logs)
    {
        await server.DisconnectClientAsync();
        await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
    }

    [Fact]
    public async Task Callback_Throws_RetriedOnNewConnection()
    {
        var logs = new FakeLoggerFactory();
        int calls = 0;
        var ports = new ConcurrentQueue<int>();
        var (server, client) = await Resilient.StartAsync(Restoring(logs, (ctx, _) =>
        {
            ports.Enqueue(ctx.Client.LocalEndPoint!.Port);
            return Interlocked.Increment(ref calls) == 1 ? Task.FromException(new ApplicationException("restore failed")) : Task.CompletedTask;
        }));
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
        }

        var failed = Assert.Single(logs.Events(1104));
        Assert.Equal("Restore", failed.Value("Phase"));
        Assert.IsType<ApplicationException>(failed.Exception);
        Assert.Equal(2, ports.Distinct().Count());              // the third connection runs the second callback
        Assert.Equal((2, 1), (logs.Events(1110).Count, logs.Events(1111).Count));
    }

    [Fact]
    public async Task Callback_Nak_RetriedOnNewConnection()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Restoring(logs, (ctx, ct) => ctx.Client.SetAsync(new AfGain(0, 5), ct)), s =>
        {
            s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Nak));
            s.Preload(new InterfaceVersion(529));
        });
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
            var failed = Assert.Single(logs.Events(1104));
            Assert.Equal("Restore", failed.Value("Phase"));
            Assert.IsType<NetSdrNakException>(failed.Exception);
            Assert.Equal(2, server.Received.Count(r => r.Code == AfGain.Code));
            Assert.Empty(logs.Events(1106));
            Assert.False(client.Completion.IsCompleted);
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
        }
    }

    [Fact]
    public async Task Callback_PersistentNak_GivesUpOnlyWhenAttemptsRunOut()
    {
        var endless = await RunAsync(attempts: null, until: l => l.Events(1104).Count >= 10);
        Assert.Empty(endless.Events(1106));
        Assert.All(endless.Events(1104), r => Assert.IsType<NetSdrNakException>(r.Exception));

        var limited = await RunAsync(attempts: 3, until: l => l.Events(1106).Count == 1);
        Assert.Equal(2, limited.Events(1104).Count);
        Assert.IsType<NetSdrNakException>(Assert.Single(limited.Events(1106)).Exception!.InnerException);

        static async Task<FakeLoggerFactory> RunAsync(int? attempts, Func<FakeLoggerFactory, bool> until)
        {
            var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
            var connector = new PipeConnector(time) { Serve = d => d.NakEverythingAsync() };
            var options = Resilient.Seam(logs, time);
            options.ResponseTimeout = TimeSpan.FromHours(1);   // 5 s fake-time steps must not time out a request the pipe NAKs in real time
            options.ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new AfGain(0, 5), ct);
            if (attempts is { } n)
                options.ReconnectAttempts = n;
            var (client, device) = await connector.StartAsync(options);
            await using (client)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                device.CloseRemote();
                await time.AdvanceUntilAsync(() => until(logs), TimeSpan.FromSeconds(5));
            }

            return logs;
        }
    }

    [Fact]
    public async Task Callback_CallsResilientClient_GivesUpWithClearMessage()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        Exception? thrown = null;
        var started = await Resilient.StartAsync(Restoring(logs, (ctx, ct) =>
        {
            try
            { _ = client!.GetAsync<InterfaceVersion>(ct); }
            catch (InvalidOperationException e) { thrown = e; throw; }   // thrown synchronously
            return Task.CompletedTask;
        }));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await started.Server.DisconnectClientAsync();
            var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.Contains("ConnectionRestored called the ResilientControlClient instead of context.Client", failure.Message);
            Assert.Same(thrown, failure.InnerException);
        }

        Assert.Equal("ConnectionRestored called the ResilientControlClient", Assert.Single(logs.Events(1106)).Value("Reason"));
    }

    [Fact]
    public async Task Callback_CatchesReentrancy_StillGivesUp()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        var started = await Resilient.StartAsync(Restoring(logs, (ctx, ct) =>
        {
            try
            { _ = client!.GetAsync<InterfaceVersion>(ct); }
            catch (InvalidOperationException) { }                       // swallowed: the client still gives up
            return Task.CompletedTask;
        }));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await started.Server.DisconnectClientAsync();
            await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        }

        Assert.Single(logs.Events(1106));
    }

    [Fact]
    public async Task Callback_FireAndForgetAfterReturn_Allowed()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        Task<InterfaceVersion>? later = null;
        var started = await Resilient.StartAsync(Restoring(logs, (ctx, _) =>
        {
            later = Task.Run(async () =>
            {
                await Eventually.ThatAsync(() => client!.IsConnected);   // after the callback returned and the link was published
                return await client!.GetAsync<InterfaceVersion>();
            });
            return Task.CompletedTask;
        }), s => s.Preload(new InterfaceVersion(529)));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await DropAndRestoreAsync(started.Server, logs);
            Assert.Equal(529, (await later!.WaitAsync(Limits.Test)).Version);
            Assert.Empty(logs.Events(1106));
        }
    }

    [Fact]
    public async Task Callback_SessionRules()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        ConnectionRestoredContext? seen = null;
        bool? connectedInside = null;
        int sessionPort = 0;
        var started = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            (seen, sessionPort, connectedInside) = (ctx, ctx.Client.LocalEndPoint!.Port, client!.IsConnected);
            await ctx.Client.DisposeAsync();                        // does nothing
            await ctx.Client.GetAsync<InterfaceVersion>(ct);
        }), s => s.Preload(new InterfaceVersion(529)));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            int oldPort = client.LocalEndPoint!.Port;
            await DropAndRestoreAsync(started.Server, logs);
            Assert.NotEqual(oldPort, sessionPort);
            Assert.False(connectedInside);
            Assert.True(client.IsConnected);
            Assert.IsAssignableFrom<IOException>(seen!.Cause);
            Assert.Throws<InvalidOperationException>(() => { _ = seen.Client.GetAsync<InterfaceVersion>(); });
        }
    }

    [Fact]
    public async Task Callback_ConnectionLostDuringIt()
    {
        var logs = new FakeLoggerFactory();
        Exception? first = null;
        var (server, client) = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            try
            { await ctx.Client.SetAsync(new AfGain(0, 5), ct); }
            catch (Exception e) { first ??= e; throw; }
        }), s => s.OnRequest(AfGain.Code, Resilient.DropOnce(s)));
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
        }

        Assert.IsAssignableFrom<IOException>(first);
        Assert.Empty(logs.Events(1100));                          // failed at once, not after a response timeout
        Assert.Equal("Restore", Assert.Single(logs.Events(1104)).Value("Phase"));
    }

    [Fact]
    public async Task CommandDuringRestore_WaitsForCallback()
    {
        var logs = new FakeLoggerFactory();
        var inCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client) = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            inCallback.TrySetResult();
            await gate.Task.WaitAsync(ct);
            await ctx.Client.SetAsync(new AfGain(0, 5), ct);
        }), s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            await server.DisconnectClientAsync();
            await inCallback.Task.WaitAsync(Limits.Test);
            var get = client.GetAsync<InterfaceVersion>();
            await Task.Delay(300);
            Assert.DoesNotContain(server.Received, r => r.Code == InterfaceVersion.Code);
            gate.SetResult();
            Assert.Equal(529, (await get.WaitAsync(Limits.Test)).Version);
            Assert.Equal(new[] { AfGain.Code, InterfaceVersion.Code }, server.Received.WithoutStatus().Select(r => r.Code));
        }
    }

    [Fact]
    public async Task Dispose_DuringCallback()
    {
        var logs = new FakeLoggerFactory();
        var inCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool tokenCancelled = false;
        Exception? afterDispose = null;
        var (server, client) = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            inCallback.TrySetResult();
            try
            { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { tokenCancelled = true; }
            try
            { await ctx.Client.GetAsync<InterfaceVersion>(); }
            catch (Exception e) { afterDispose = e; }
        }));
        await using (server)
        {
            await server.DisconnectClientAsync();
            await inCallback.Task.WaitAsync(Limits.Test);
            await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
            Assert.True(tokenCancelled);
            Assert.IsType<ObjectDisposedException>(afterDispose);
            Assert.True(client.Completion.IsCompletedSuccessfully);
            Assert.Empty(logs.Events(1106));
            await Loopback.AssertServerFreeAsync(server);
        }
    }

    [Fact]
    public async Task Dispose_FromInsideCallback_NoDeadlock()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        bool disposedInside = false;
        var started = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            await client!.DisposeAsync().AsTask().WaitAsync(Limits.Test);   // returns without waiting for the supervisor
            disposedInside = true;
        }));
        client = started.Client;
        await using (started.Server)
        {
            await started.Server.DisconnectClientAsync();
            await Eventually.ThatAsync(() => disposedInside);
            Assert.True(client.Completion.IsCompletedSuccessfully);
            Assert.False(client.IsConnected);
            Assert.Empty(logs.Events(1105));
            Assert.Empty(logs.Events(1106));
            await Loopback.AssertServerFreeAsync(started.Server);
        }
    }

    // Review Focus 4.
    [Theory]
    [InlineData("throw")]
    [InlineData("null")]
    public async Task Callback_SyncThrowOrNullTask_FailsOnlyTheAttempt(string kind)
    {
        var logs = new FakeLoggerFactory();
        int calls = 0;
        var (server, client) = await Resilient.StartAsync(Restoring(logs, (ctx, ct) =>
            Interlocked.Increment(ref calls) > 1 ? Task.CompletedTask
            : kind == "throw" ? throw new ApplicationException("not async") : (Task)null!));
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
            Assert.Equal("Restore", Assert.Single(logs.Events(1104)).Value("Phase"));
            Assert.Equal(2, calls);
            Assert.Empty(logs.Events(1106));
            Assert.False(client.Completion.IsCompleted);
        }
    }
}
