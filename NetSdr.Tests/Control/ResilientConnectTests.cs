using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetSdr.Control;
using NetSdr.Items;

namespace NetSdr.Tests.Control;

public class ResilientConnectTests
{
    static readonly Dictionary<string, Action<ResilientControlClientOptions>> Invalid = new()
    {
        ["ResponseTimeout 0"] = o => o.ResponseTimeout = TimeSpan.Zero,
        ["ResponseTimeout Infinite"] = o => o.ResponseTimeout = Timeout.InfiniteTimeSpan,
        ["LateReplyTimeout 0"] = o => o.LateReplyTimeout = TimeSpan.Zero,
        ["Sum above Int32"] = o => (o.ResponseTimeout, o.LateReplyTimeout) = (TimeSpan.FromMilliseconds(int.MaxValue), TimeSpan.FromMilliseconds(1)),
        ["CommandTimeout 0"] = o => o.CommandTimeout = TimeSpan.Zero,
        ["HeartbeatInterval -1 s"] = o => o.HeartbeatInterval = TimeSpan.FromSeconds(-1),
        ["ConnectTimeout 0"] = o => o.ConnectTimeout = TimeSpan.Zero,
        ["ReconnectAttempts 0"] = o => o.ReconnectAttempts = 0,
        ["UnsolicitedCapacity 0"] = o => o.UnsolicitedCapacity = 0,
    };

    public static TheoryData<string> InvalidNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (string name in Invalid.Keys) names.Add(name);
            return names;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Options_Invalid(string name)
    {
        var options = new ResilientControlClientOptions();
        Invalid[name](options);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), options); });
    }

    [Fact]
    public void Options_Defaults()
    {
        var o = new ResilientControlClientOptions();
        Assert.Equal((2, 13, 30, 5, 5), ((int)o.ResponseTimeout.TotalSeconds, (int)o.LateReplyTimeout.TotalSeconds,
            (int)o.CommandTimeout.TotalSeconds, (int)o.HeartbeatInterval.TotalSeconds, (int)o.ConnectTimeout.TotalSeconds));
        Assert.Equal((int.MaxValue, 256, true), (o.ReconnectAttempts, o.UnsolicitedCapacity, o.UseJitter));
        Assert.Same(NullLoggerFactory.Instance, o.LoggerFactory);
    }

    [Fact]
    public async Task FirstConnect()
    {
        // Refused: thrown, nothing keeps running, nothing logged as connected.
        var logs = new FakeLoggerFactory();
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThrowsAsync<SocketException>(() => ResilientControlClient.ConnectAsync(refused.ConnectAsync, "pipe", Resilient.Seam(logs), default));
        await Task.Delay(1200);                                  // longer than the 1 s floor of a reconnect attempt
        Assert.Equal(1, refused.Attempts);
        Assert.Empty(logs.Events(1109));

        // ConnectTimeout, and the caller's token.
        var hanging = new PipeConnector { Before = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var slow = Resilient.Seam();
        slow.ConnectTimeout = TimeSpan.FromMilliseconds(200);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => ResilientControlClient.ConnectAsync(hanging.ConnectAsync, "pipe", slow, default));
        Assert.StartsWith("No TCP connection to pipe within", timeout.Message);
        using var cancel = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ResilientControlClient.ConnectAsync(hanging.ConnectAsync, "pipe", Resilient.Seam(), cancel.Token));

        // A device that accepts but never answers the verification.
        var mute = new PipeConnector { Serve = _ => Task.CompletedTask };
        await Assert.ThrowsAsync<TimeoutException>(() => ResilientControlClient.ConnectAsync(mute.ConnectAsync, "pipe", Resilient.Seam(), default));
        Assert.False((await mute.NextAsync()).Client.IsConnected);

        // Success: the bare server NAKs the verification, 1109 is written, ConnectionRestored is not called.
        int callbacks = 0;
        var ok = new FakeLoggerFactory();
        var options = Resilient.Fast(ok);
        options.ConnectionRestored = (_, _) => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; };
        var (server, client) = await Resilient.StartAsync(options);
        await using (server)
        await using (client)
        {
            Assert.True(client.IsConnected);
            Assert.Equal(StatusCodes.Code, server.Received[0].Code);
        }

        var connected = Assert.Single(ok.Events(1109));
        Assert.Equal((LogLevel.Information, "NetSdr.Control.ResilientControlClient"), (connected.Level, connected.Category));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task Dispose_Basic_CompletesUnsolicitedAfterCompletion()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        {
            var completedFirst = client.Unsolicited.Completion.ContinueWith(_ => client.Completion.IsCompletedSuccessfully);
            await client.DisposeAsync();
            await client.DisposeAsync();
            Assert.True(client.Completion.IsCompletedSuccessfully);
            Assert.True(await completedFirst.WaitAsync(Limits.Test));
            Assert.False(client.IsConnected);
            Assert.Throws<ObjectDisposedException>(() => { _ = client.GetAsync<InterfaceVersion>(); });
            await Loopback.AssertServerFreeAsync(server);
        }

        Assert.Equal(LogLevel.Information, Assert.Single(logs.Events(1112)).Level);
        Assert.Empty(logs.Events(1103));                                              // the disposal is not a loss
        Assert.Empty(logs.Events(1104));                                              // and nothing reconnects after it
    }

    [Fact]
    public async Task ResilientLogging_NullLoggerFactory_Works()
    {
        var (server, client) = await Resilient.StartAsync(new ResilientControlClientOptions(), s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
        }
    }
}
