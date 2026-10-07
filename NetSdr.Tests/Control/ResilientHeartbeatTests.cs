using Microsoft.Extensions.Logging;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientHeartbeatTests
{
    static int StatusRequests(NetSdrTestServer server) => server.Received.Count(r => r.Code == StatusCodes.Code);

    [Fact]
    public async Task Heartbeat_IdleNak_Alive()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));   // the bare server NAKs 0x0005
        await using (server)
        await using (client)
        {
            await Task.Delay(1000);                                                   // ten intervals of silence
            Assert.InRange(StatusRequests(server) - 1, 5, 11);                        // minus the verification
            Assert.Empty(logs.Events(1101));
            Assert.Empty(logs.Events(1103));
        }
    }

    [Fact]
    public async Task Heartbeat_SkippedWhileTrafficFlows()
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            for (int i = 0; i < 20; i++)
            {
                await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test);
                await Task.Delay(50);
            }

            Assert.Equal(1, StatusRequests(server));
        }
    }

    [Fact]
    public async Task Heartbeat_Silent_UnpluggedCable()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            server.OnRequest(StatusCodes.Code, _ => ControlReply.Silent);           // after the verification passed
            await Eventually.ThatAsync(() => logs.Events(1101).Count == 1);
            int written = StatusRequests(server);
            await Task.Delay(300);                                                    // still before the 750 ms deadline
            Assert.Equal(written, StatusRequests(server));                            // no second heartbeat while one is unanswered
            await Eventually.ThatAsync(() => logs.Events(1103).Count == 1);
            Assert.Single(logs.Events(1102));
            Assert.Single(logs.Events(1101));
            await Eventually.ThatAsync(() => logs.Events(1104).Any(r => r.Value("Phase") == "Verify"));
        }
    }

    [Fact]
    public async Task Heartbeat_LateReply_NoReconnect()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            server.OnRequest(StatusCodes.Code, _ => ControlReply.Bytes(new[] { StatusCodes.Idle }).After(TimeSpan.FromMilliseconds(250)));
            await Eventually.ThatAsync(() => logs.Events(1108).Count >= 1);
            Assert.Equal(("Heartbeat", "Reply"), (logs.Events(1108)[0].Value("Owner"), logs.Events(1108)[0].Value("Outcome")));
            Assert.NotEmpty(logs.Events(1101));
            await Task.Delay(500);
            Assert.Empty(logs.Events(1103));
        }
    }

    [Fact]
    public async Task Heartbeat_Disabled()
    {
        var options = Resilient.Fast();
        options.HeartbeatInterval = Timeout.InfiniteTimeSpan;
        var (server, client) = await Resilient.StartAsync(options);
        await using (server)
        await using (client)
        {
            await Task.Delay(500);
            Assert.Equal(1, StatusRequests(server));
        }
    }

    // Any failure of the heartbeat loop other than the disposal makes the client give up (spec 7.1), and the connection
    // it was watching is still open then: it is closed at once, not left to DisposeAsync.
    [Fact]
    public async Task Heartbeat_LoopFails_GivesUp_AndClosesTheConnection()
    {
        var logs = new FakeLoggerFactory();
        var time = new FailingTimersTimeProvider();
        var options = Resilient.Seam(logs, time);
        options.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        var (client, device) = await new PipeConnector { Serve = d => d.NakEverythingAsync() }.StartAsync(options);
        await using (client)
        {
            time.Armed = true;                                                        // the heartbeat's next wait fails
            var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            await device.Client.Completion.WaitAsync(Limits.Test);                    // the watched connection is closed
            Assert.Single(logs.Events(1106));
        }
    }

    /// <summary>The system clock, except that once <see cref="Armed"/> every new timer fails.</summary>
    private sealed class FailingTimersTimeProvider : TimeProvider
    {
        private volatile bool _armed;

        public bool Armed
        {
            get => _armed;
            set => _armed = value;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _armed ? throw new InvalidOperationException("The clock failed.") : System.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task ResilientLogging_LevelsEventIdsCategories()
    {
        const string Outer = "NetSdr.Control.ResilientControlClient", Inner = "NetSdr.Control.NetSdrControlClient";

        // A busy reply, a silent heartbeat and a silent verification, then a NAK: 1100, 1107, 1101, 1102, 1103, 1104, 1105.
        var logs = new FakeLoggerFactory(LogLevel.Debug);
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs),
            s => s.OnRequest(RfGain.Code, Resilient.Once(ControlReply.Echo.After(TimeSpan.FromMilliseconds(250)))));
        await using (server)
        {
            await client.SetAsync(new RfGain(0, -20)).WaitAsync(Limits.Test);
            // Echoed at once: the inner client writes Debug 1003 and 1004 with Item = RfGain.
            Assert.Equal(-10, (await client.SetAsync(new RfGain(0, -10)).WaitAsync(Limits.Test)).GainDb);
            server.OnRequest(StatusCodes.Code, Resilient.FirstTimes(2, ControlReply.Silent, ControlReply.Nak));
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        }

        void Expect(FakeLoggerFactory from, int id, LogLevel level, string category)
        {
            Assert.NotEmpty(from.Events(id));
            Assert.All(from.Events(id), r => Assert.Equal((level, category), (r.Level, r.Category)));
        }

        foreach (int id in new[] { 1100, 1101, 1102, 1103, 1104 }) Expect(logs, id, LogLevel.Warning, Outer);
        foreach (int id in new[] { 1105, 1109, 1112 }) Expect(logs, id, LogLevel.Information, Outer);
        Expect(logs, 1107, LogLevel.Debug, Outer);
        foreach (int id in new[] { 1000, 1001, 1003, 1004, 1005, 1006 }) Expect(logs, id, LogLevel.Debug, Inner);
        Assert.Contains(logs.Events(1003), r => r.Value("Item") == "RfGain");
        Assert.Contains(logs.Events(1004), r => r.Value("Item") == "RfGain");
        Assert.Empty(logs.Events(1010).Concat(logs.Events(1011)).Concat(logs.Events(1106)));
        Assert.Single(logs.Events(1103));                                             // the unresponsive line only: the disposal is not a loss
        Assert.Single(logs.Events(1112));

        // The device closes the connection: the inner fault is Debug; Trace shows the frames.
        var traced = new FakeLoggerFactory(LogLevel.Trace);
        var (server2, client2) = await Resilient.StartAsync(Resilient.Fast(traced));
        await using (server2)
        await using (client2)
        {
            await server2.DisconnectClientAsync().WaitAsync(Limits.Test);
            await Eventually.ThatAsync(() => traced.Events(1105).Count == 1);
        }

        Expect(traced, 1002, LogLevel.Debug, Inner);
        Assert.NotEmpty(traced.Events(1010));
        Assert.NotEmpty(traced.Events(1011));
        Assert.Single(traced.Events(1103));                                           // the device's drop only, not the disposal

        // Giving up: Error 1106 exactly once.
        var gaveUp = new FakeLoggerFactory();
        var options = Resilient.Seam(gaveUp);
        options.ReconnectAttempts = 1;
        var (lost, device) = await new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() }.StartAsync(options);
        device.CloseRemote();
        await Assert.ThrowsAsync<IOException>(() => lost.Completion.WaitAsync(Limits.Test));
        await lost.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        Expect(gaveUp, 1106, LogLevel.Error, Outer);
        Assert.Single(gaveUp.Events(1106));
    }
}
