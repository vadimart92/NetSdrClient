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
                await client.GetAsync<InterfaceVersion>();
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
            await client.DisposeAsync();
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

        // The device closes the connection: the inner fault is Debug; Trace shows the frames.
        var traced = new FakeLoggerFactory(LogLevel.Trace);
        var (server2, client2) = await Resilient.StartAsync(Resilient.Fast(traced));
        await using (server2)
        await using (client2)
        {
            await server2.DisconnectClientAsync();
            await Eventually.ThatAsync(() => traced.Events(1105).Count == 1);
        }

        Expect(traced, 1002, LogLevel.Debug, Inner);
        Assert.NotEmpty(traced.Events(1010));
        Assert.NotEmpty(traced.Events(1011));

        // Giving up: Error 1106 exactly once.
        var gaveUp = new FakeLoggerFactory();
        var options = Resilient.Seam(gaveUp);
        options.ReconnectAttempts = 1;
        var (lost, device) = await new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() }.StartAsync(options);
        device.CloseRemote();
        await Assert.ThrowsAsync<IOException>(() => lost.Completion.WaitAsync(Limits.Test));
        await lost.DisposeAsync();
        Expect(gaveUp, 1106, LogLevel.Error, Outer);
        Assert.Single(gaveUp.Events(1106));
    }
}
