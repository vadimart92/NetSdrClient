using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientConnectTests
{
    private static readonly Dictionary<string, Action<ResilientControlClientOptions>> Invalid = new()
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
        ["RebootTimeout 0"] = o => o.RebootTimeout = TimeSpan.Zero,
        ["RebootTimeout Infinite"] = o => o.RebootTimeout = Timeout.InfiniteTimeSpan,
        ["KeepAlive 0"] = o => o.KeepAlive = TimeSpan.Zero,
        ["KeepAlive 999 ms"] = o => o.KeepAlive = TimeSpan.FromMilliseconds(999),
    };

    public static IEnumerable<string> InvalidNames => Invalid.Keys;

    [TestCaseSource(nameof(InvalidNames))]
    public void Options_Invalid(string name)
    {
        var options = new ResilientControlClientOptions();
        Invalid[name](options);
        Assert.That(
            () => { _ = ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), options); },
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Options_Defaults()
    {
        var o = new ResilientControlClientOptions();
        Assert.That(
            ((int)o.ResponseTimeout.TotalSeconds, (int)o.LateReplyTimeout.TotalSeconds, (int)o.CommandTimeout.TotalSeconds,
                (int)o.HeartbeatInterval.TotalSeconds, (int)o.ConnectTimeout.TotalSeconds),
            Is.EqualTo((2, 13, 30, 5, 5)));
        Assert.That((o.ReconnectAttempts, o.UnsolicitedCapacity, o.UseJitter), Is.EqualTo((int.MaxValue, 256, true)));
        Assert.That(o.LoggerFactory, Is.SameAs(NullLoggerFactory.Instance));
        Assert.That(o.Rebooter, Is.Null);
        Assert.That(o.RecoveryPolicy, Is.Null);
        Assert.That(o.ConnectAttempts, Is.Null);
        Assert.That((int)o.RebootTimeout.TotalSeconds, Is.EqualTo(10));
        Assert.That(o.KeepAlive, Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [TestCase(3)]
    [TestCase(null)]
    public async Task KeepAlive_ReachesTheInnerConnection(int? seconds)
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        NetSdrControlClient? inner = null;
        var options = Resilient.Fast();
        options.KeepAlive = seconds is { } s ? TimeSpan.FromSeconds(s) : null;
        await using var client = await ResilientControlClient.ConnectAsync(
            (c, ct) =>
            {
                inner = c;
                return c.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), ct);
            },
            "loopback", options, default);

        Socket socket = inner!.Socket!;
        Assert.That(
            (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)! != 0,
            Is.EqualTo(seconds is not null));
        if (seconds is not null)
        {
            Assert.That((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!, Is.EqualTo(seconds));
        }
    }

    [Test]
    public async Task FirstConnect()
    {
        // Refused: thrown, nothing keeps running, nothing logged as connected.
        var logs = new FakeLoggerFactory();
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThatAsync(
            () => ResilientControlClient.ConnectAsync(refused.ConnectAsync, "pipe", Resilient.Seam(logs), default),
            Throws.TypeOf<SocketException>());
        await Task.Delay(1200);                                  // longer than the 1 s floor of a reconnect attempt
        Assert.That(refused.Attempts, Is.EqualTo(1));
        Assert.That(logs.Events(1109), Is.Empty);

        // ConnectTimeout, and the caller's token.
        var hanging = new PipeConnector { Before = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var slow = Resilient.Seam();
        slow.ConnectTimeout = TimeSpan.FromMilliseconds(200);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => ResilientControlClient.ConnectAsync(hanging.ConnectAsync, "pipe", slow, default));
        Assert.That(timeout.Message, Does.StartWith("No TCP connection to pipe within"));
        using var cancel = new CancellationTokenSource(200);
        await Assert.ThatAsync(
            () => ResilientControlClient.ConnectAsync(hanging.ConnectAsync, "pipe", Resilient.Seam(), cancel.Token),
            Throws.InstanceOf<OperationCanceledException>());

        // A device that accepts but never answers the verification.
        var mute = new PipeConnector { Serve = _ => Task.CompletedTask };
        await Assert.ThatAsync(
            () => ResilientControlClient.ConnectAsync(mute.ConnectAsync, "pipe", Resilient.Seam(), default),
            Throws.TypeOf<TimeoutException>());
        Assert.That((await mute.NextAsync()).Client.IsConnected, Is.False);

        // Success: the bare server NAKs the verification, 1109 is written, ConnectionRestored is not called.
        int callbacks = 0;
        var ok = new FakeLoggerFactory();
        var options = Resilient.Fast(ok);
        options.ConnectionRestored = (_, _) => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; };
        var (server, client) = await Resilient.StartAsync(options);
        await using (server)
        await using (client)
        {
            Assert.That(client.IsConnected, Is.True);
            Assert.That(server.Received[0].Code, Is.EqualTo(StatusCodes.Code));
        }

        var connected = ok.Events(1109).Sole();
        Assert.That((connected.Level, connected.Category), Is.EqualTo((LogLevel.Information, "NetSdr.Control.ResilientControlClient")));
        Assert.That(callbacks, Is.EqualTo(0));
    }

    [Test]
    public async Task Dispose_Basic_CompletesUnsolicitedAfterCompletion()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        {
            var completedFirst = client.Unsolicited.Completion.ContinueWith(_ => client.Completion.IsCompletedSuccessfully);
            await client.DisposeAsync();
            await client.DisposeAsync();
            Assert.That(client.Completion.IsCompletedSuccessfully, Is.True);
            Assert.That(await completedFirst.WaitAsync(Limits.Test), Is.True);
            Assert.That(client.IsConnected, Is.False);
            Assert.That(() => { _ = client.GetAsync<InterfaceVersion>(); }, Throws.TypeOf<ObjectDisposedException>());
            await Loopback.AssertServerFreeAsync(server);
        }

        Assert.That(logs.Events(1112).Sole().Level, Is.EqualTo(LogLevel.Information));
        Assert.That(logs.Events(1103), Is.Empty);                                     // the disposal is not a loss
        Assert.That(logs.Events(1104), Is.Empty);                                     // and nothing reconnects after it
    }

    [Test]
    public async Task ResilientLogging_NullLoggerFactory_Works()
    {
        var (server, client) = await Resilient.StartAsync(new ResilientControlClientOptions(), s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            Assert.That((await client.GetAsync<InterfaceVersion>()).Version, Is.EqualTo(529));
        }
    }
}
