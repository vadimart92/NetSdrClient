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

    [Test]
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

        var failed = logs.Events(1104).Sole();
        Assert.That(failed.Value("Phase"), Is.EqualTo("Restore"));
        Assert.That(failed.Exception, Is.TypeOf<ApplicationException>());
        Assert.That(ports.Distinct().Count(), Is.EqualTo(2));   // the third connection runs the second callback
        Assert.That((logs.Events(1110).Count, logs.Events(1111).Count), Is.EqualTo((2, 1)));
    }

    [Test]
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
            var failed = logs.Events(1104).Sole();
            Assert.That(failed.Value("Phase"), Is.EqualTo("Restore"));
            Assert.That(failed.Exception, Is.TypeOf<NetSdrNakException>());
            Assert.That(server.Received.Count(r => r.Code == AfGain.Code), Is.EqualTo(2));
            Assert.That(logs.Events(1106), Is.Empty);
            Assert.That(client.Completion.IsCompleted, Is.False);
            Assert.That((await client.GetAsync<InterfaceVersion>()).Version, Is.EqualTo(529));
        }
    }

    [Test]
    public async Task Callback_PersistentNak_GivesUpOnlyWhenAttemptsRunOut()
    {
        var endless = await RunAsync(attempts: null, until: l => l.Events(1104).Count >= 10);
        Assert.That(endless.Events(1106), Is.Empty);
        Assert.That(endless.Events(1104).Select(r => r.Exception), Has.All.TypeOf<NetSdrNakException>());

        var limited = await RunAsync(attempts: 3, until: l => l.Events(1106).Count == 1);
        Assert.That(limited.Events(1104), Has.Count.EqualTo(2));
        Assert.That(limited.Events(1106).Sole().Exception!.InnerException, Is.TypeOf<NetSdrNakException>());

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

    [Test]
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
            Assert.That(failure.Message, Does.Contain("ConnectionRestored called the ResilientControlClient instead of context.Client"));
            Assert.That(failure.InnerException, Is.SameAs(thrown));
        }

        Assert.That(logs.Events(1106).Sole().Value("Reason"), Is.EqualTo("ConnectionRestored called the ResilientControlClient"));
    }

    [Test]
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
            await Assert.ThatAsync(() => client.Completion.WaitAsync(Limits.Test), Throws.TypeOf<IOException>());
        }

        Assert.That(logs.Events(1106), Has.Exactly(1).Items);
    }

    [Test]
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
            Assert.That((await later!.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
            Assert.That(logs.Events(1106), Is.Empty);
        }
    }

    [Test]
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
            Assert.That(sessionPort, Is.Not.EqualTo(oldPort));
            Assert.That(connectedInside, Is.False);
            Assert.That(client.IsConnected, Is.True);
            Assert.That(seen!.Cause, Is.InstanceOf<IOException>());
            Assert.That(() => { _ = seen.Client.GetAsync<InterfaceVersion>(); }, Throws.TypeOf<InvalidOperationException>());
        }
    }

    [Test]
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

        Assert.That(first, Is.InstanceOf<IOException>());
        Assert.That(logs.Events(1100), Is.Empty);                 // failed at once, not after a response timeout
        Assert.That(logs.Events(1104).Sole().Value("Phase"), Is.EqualTo("Restore"));
    }

    [Test]
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
            Assert.That(server.Received.Select(r => r.Code), Does.Not.Contain(InterfaceVersion.Code));
            gate.SetResult();
            Assert.That((await get.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
            Assert.That(server.Received.WithoutStatus().Select(r => r.Code), Is.EqualTo(new[] { AfGain.Code, InterfaceVersion.Code }));
        }
    }

    [Test]
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
            Assert.That(tokenCancelled, Is.True);
            Assert.That(afterDispose, Is.TypeOf<ObjectDisposedException>());
            Assert.That(client.Completion.IsCompletedSuccessfully, Is.True);
            Assert.That(logs.Events(1106), Is.Empty);
            await Loopback.AssertServerFreeAsync(server);
        }
    }

    [Test]
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
            Assert.That(client.Completion.IsCompletedSuccessfully, Is.True);
            Assert.That(client.IsConnected, Is.False);
            Assert.That(logs.Events(1105), Is.Empty);
            Assert.That(logs.Events(1106), Is.Empty);
            await Loopback.AssertServerFreeAsync(started.Server);
        }
    }

    // Review Focus 4.
    [TestCase("throw")]
    [TestCase("null")]
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
            Assert.That(logs.Events(1104).Sole().Value("Phase"), Is.EqualTo("Restore"));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(logs.Events(1106), Is.Empty);
            Assert.That(client.Completion.IsCompleted, Is.False);
        }
    }
}
