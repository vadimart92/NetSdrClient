using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientHeartbeatTests
{
    static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    static int StatusRequests(NetSdrTestServer server) => server.Received.Count(r => r.Code == StatusCodes.Code);

    /// <summary>Seam options on fake time with the heartbeat of <see cref="Resilient.Fast"/>.</summary>
    static ResilientControlClientOptions FakeHeartbeat(FakeLoggerFactory logs, TimeProvider time)
    {
        var options = Resilient.Seam(logs, time);
        options.HeartbeatInterval = Interval;
        return options;
    }

    /// <summary>
    /// How many times the heartbeat has waited for <see cref="Interval"/> of silence: its delay is the only timer of
    /// that length (the inner response timeout is 150 ms, ConnectTimeout 5 s).
    /// </summary>
    static int SilenceWaits(CountingTimeProvider time) => time.TimerDueTimes.Count(due => due == Interval);

    /// <summary>Records every request the device reads and NAKs the first <paramref name="answered"/> of them; the rest get no reply.</summary>
    static async Task RecordAndNakAsync(PipeDevice device, ConcurrentQueue<byte[]> requests, int answered = int.MaxValue)
    {
        try
        {
            for (int n = 1; ; n++)
            {
                requests.Enqueue(await device.ReadRequestAsync());
                if (n <= answered) await device.SendAsync(Resilient.Nak);
            }
        }
        catch (Exception)
        {
            // The pipe is gone.
        }
    }

    [Fact]
    public async Task Heartbeat_IdleNak_Alive()
    {
        var (logs, fake) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var time = new CountingTimeProvider(fake);
        var requests = new ConcurrentQueue<byte[]>();
        var (client, _) = await new PipeConnector(time) { Serve = d => RecordAndNakAsync(d, requests) }
            .StartAsync(FakeHeartbeat(logs, time));
        await using (client)
        {
            // Ten intervals of silence. Time moves only while the heartbeat waits for its interval, so each interval
            // sends exactly one Get 0x0005, and none goes out before the interval has passed.
            for (int i = 1; i <= 10; i++)
            {
                await Eventually.ThatAsync(() => SilenceWaits(time) == i);
                Assert.Equal(i, requests.Count);                                      // the verification and i - 1 heartbeats
                fake.Advance(Interval);
            }

            await Eventually.ThatAsync(() => SilenceWaits(time) == 11);
            Assert.Equal(11, requests.Count);
            Assert.All(requests, r => Assert.Equal(Hex.Parse(Resilient.GetStatus), r));
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
        var (logs, fake) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var time = new CountingTimeProvider(fake);
        var requests = new ConcurrentQueue<byte[]>();
        int connections = 0;
        // The first connection answers its verification and then falls silent; later ones answer nothing.
        var connector = new PipeConnector(time)
        {
            Serve = d => RecordAndNakAsync(d, requests, answered: Interlocked.Increment(ref connections) == 1 ? 1 : 0),
        };
        var (client, _) = await connector.StartAsync(FakeHeartbeat(logs, time));
        await using (client)
        {
            // Fake time moves only once the timer it is meant to fire exists, so it never runs ahead of the client.
            var responseTimeout = TimeSpan.FromMilliseconds(150);
            int responseTimers = time.TimerDueTimes.Count(due => due == responseTimeout);   // the verification's, if any
            await Eventually.ThatAsync(() => SilenceWaits(time) == 1);
            fake.Advance(Interval);                                                   // the heartbeat is written
            await Eventually.ThatAsync(() => requests.Count == 2);
            await Eventually.ThatAsync(() => time.TimerDueTimes.Count(due => due == responseTimeout) == responseTimers + 1);
            fake.Advance(responseTimeout);
            await Eventually.ThatAsync(() => logs.Events(1101).Count == 1);
            // The late-reply deadline is 750 ms after the write, 600 ms from here. Up to it nothing else is written:
            // no second heartbeat while one is unanswered, and the next connection waits for the rest of the 1 s floor.
            await Eventually.ThatAsync(() => time.TimerDueTimes.Contains(TimeSpan.FromMilliseconds(600)));
            fake.Advance(TimeSpan.FromMilliseconds(600));
            Assert.Single(logs.Events(1102));                                         // Expire runs inside Advance
            Assert.Equal(2, requests.Count);                                          // the verification and the one heartbeat
            await Eventually.ThatAsync(() => logs.Events(1103).Count == 1);
            Assert.Single(logs.Events(1101));
            await fake.AdvanceUntilAsync(() => logs.Events(1104).Any(r => r.Value("Phase") == "Verify"), TimeSpan.FromMilliseconds(10));
            Assert.Single(logs.Events(1102));
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
        var (logs, fake) = (new FakeLoggerFactory(), new FakeTimeProvider());
        // Only the heartbeat's waits fail: the inner response timeout (150 ms) and ConnectTimeout are longer.
        var time = new FailingTimersTimeProvider(fake, failUpTo: Interval);
        var requests = new ConcurrentQueue<byte[]>();
        var (client, device) = await new PipeConnector(time) { Serve = d => RecordAndNakAsync(d, requests) }
            .StartAsync(FakeHeartbeat(logs, time));
        await using (client)
        {
            time.Armed = true;                                                        // the first wait exists already
            fake.Advance(Interval);                                                   // one probe, its NAK, then the next wait fails
            var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            await device.Client.Completion.WaitAsync(Limits.Test);                    // the watched connection is closed
            Assert.Equal(2, requests.Count);                                          // the verification and the one probe
            Assert.Equal("Gave up on pipe: watching the connection failed.", failure.Message);
            var gaveUp = Assert.Single(logs.Events(1106));
            Assert.Equal(("heartbeat failed", "0"), (gaveUp.Value("Reason"), gaveUp.Value("Attempts")));
        }
    }

    // A loss that was recovered from is over: a later failure of the heartbeat names neither its attempts nor
    // "attempts exhausted".
    [Fact]
    public async Task Heartbeat_LoopFails_AfterAReconnection_GivesUpForTheHeartbeat()
    {
        var (logs, fake) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var time = new FailingTimersTimeProvider(fake, failUpTo: Interval);
        var (client, device) = await new PipeConnector(time) { Serve = d => d.NakEverythingAsync() }
            .StartAsync(FakeHeartbeat(logs, time));
        await using (client)
        {
            device.CloseRemote();
            await fake.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, TimeSpan.FromMilliseconds(50));
            Assert.Equal("1", logs.Events(1105)[0].Value("Attempts"));
            time.Armed = true;
            await fake.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, TimeSpan.FromMilliseconds(10));
            var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.Equal("Gave up on pipe: watching the connection failed.", failure.Message);
            var gaveUp = Assert.Single(logs.Events(1106));
            Assert.Equal(("heartbeat failed", "0"), (gaveUp.Value("Reason"), gaveUp.Value("Attempts")));
        }
    }

    /// <summary><c>inner</c>, except that once <see cref="Armed"/> every new timer due within <c>failUpTo</c> fails.</summary>
    private sealed class FailingTimersTimeProvider(TimeProvider inner, TimeSpan failUpTo) : TimeProvider
    {
        private volatile bool _armed;

        public bool Armed
        {
            get => _armed;
            set => _armed = value;
        }

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _armed && dueTime <= failUpTo
                ? throw new InvalidOperationException("The clock failed.")
                : inner.CreateTimer(callback, state, dueTime, period);
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
