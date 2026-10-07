using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class ResilientLineTests
{
    private static readonly TimeSpan Late = TimeSpan.FromMilliseconds(250);   // past ResponseTimeout 150 ms, well before the 750 ms deadline

    [Fact]
    public async Task BusyDevice_LateReplyAdopted_NoResend()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs),
            s => s.OnRequest(RfGain.Code, Resilient.Once(ControlReply.Echo.After(Late))));
        await using (server)
        await using (client)
        {
            var local = client.LocalEndPoint;
            Assert.Equal(-20, (await client.SetAsync(new RfGain(0, -20)).WaitAsync(Limits.Test)).GainDb);
            Assert.Single(server.Received, r => r.Code == RfGain.Code);
            Assert.Equal("NoReplyWaitingForLateReply", Assert.Single(logs.Events(1100)).Value("Reason"));
            Assert.Equal("Reply", Assert.Single(logs.Events(1107)).Value("Outcome"));
            Assert.Empty(logs.Events(1103));
            Assert.Equal(local, client.LocalEndPoint);
        }
    }

    [Fact]
    public async Task BusyDevice_NextCommandForOtherItem_GetsItsOwnReply()
    {
        var delayed = new ConcurrentDictionary<byte, bool>();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s =>
        {
            s.OnRequest(RfGain.Code, Resilient.Once(ControlReply.Echo.After(Late)));
            s.Preload(new InterfaceVersion(529));
            s.OnRequest(FirmwareVersion.Code, r =>
            {
                byte id = r.Payload.Span[0];
                var reply = ControlReply.Bytes(new byte[] { id, (byte)(100 + id), 0 });
                return delayed.TryAdd(id, true) ? reply.After(Late) : reply;
            });
        });
        await using (server)
        await using (client)
        {
            var set = client.SetAsync(new RfGain(0, -20));
            var get = client.GetAsync<InterfaceVersion>();      // queued behind the unanswered Set
            Assert.Equal(-20, (await set.WaitAsync(Limits.Test)).GainDb);
            Assert.Equal(529, (await get.WaitAsync(Limits.Test)).Version);
            for (byte id = 0; id <= 3; id++)                     // as ReadFirmwareAsync asks, each answered late once
            {
                var reply = await client.SendAsync(RequestType.Get, FirmwareVersion.Code, new[] { id }).WaitAsync(Limits.Test);
                Assert.Equal(new byte[] { id, (byte)(100 + id), 0 }, reply.Payload.ToArray());
            }

            Assert.Equal(4, server.Received.Count(r => r.Code == FirmwareVersion.Code));
        }
    }

    [Fact]
    public async Task LateNak_AttributedToItsOwnRequest()
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s =>
        {
            s.OnRequest(SerialNumber.Code, _ => ControlReply.Nak.After(Late));
            s.Preload(new InterfaceVersion(529));
        });
        await using (server)
        await using (client)
        {
            var nak = await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<SerialNumber>().WaitAsync(Limits.Test));
            Assert.Equal(SerialNumber.Code, nak.Code);
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test)).Version);
            var options = new IdentificationOptions { IncludeStandardProbes = false };
            options.Probes.Add(Probes.Item<SerialNumber>());
            options.Probes.Add(Probes.Item<InterfaceVersion>());
            var identity = await DeviceIdentity.ReadAsync(client, options).WaitAsync(Limits.Test);
            Assert.Equal(SerialNumber.Code, Assert.Single(identity.Unsupported));
            Assert.Equal(2, server.Received.Count(r => r.Code == SerialNumber.Code));   // one per call, none resent
        }
    }

    [Fact]
    public async Task NoWriteWhileUnanswered()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            Assert.Equal(Hex.Parse("04 20 09 00"), await device.ReadRequestAsync());
            await Eventually.ThatAsync(() => logs.Events(1100).Count == 1);   // A timed out; its next attempt adopts
            var b = client.GetAsync<InterfaceVersion>();
            var next = device.ReadRequestAsync();
            await Task.Delay(300);
            Assert.False(next.IsCompleted);
            await device.SendAsync(Resilient.ProductReply);
            Assert.Equal(Hex.Parse("04 20 03 00"), await next.WaitAsync(Limits.Test));
            await device.SendAsync(Resilient.VersionReply);
            Assert.Equal(0x03524453u, (await a.WaitAsync(Limits.Test)).Value);
            Assert.Equal(529, (await b.WaitAsync(Limits.Test)).Version);
        }
    }

    [Fact]
    public async Task ForeignReply_NotRetried_LineWaitsForRealReply()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            await device.ReadRequestAsync();
            await device.SendAsync(Resilient.VersionReply);                    // Response 0x0003: foreign to A
            await Assert.ThrowsAsync<NetSdrProtocolException>(() => a.WaitAsync(Limits.Test));
            var b = client.GetAsync<InterfaceVersion>();
            var next = device.ReadRequestAsync();
            await Task.Delay(300);
            Assert.False(next.IsCompleted);
            await device.SendAsync(Resilient.ProductReply);                    // A's real reply frees the line
            Assert.Equal(Hex.Parse("04 20 03 00"), await next.WaitAsync(Limits.Test));
            await device.SendAsync(Resilient.VersionReply);
            Assert.Equal(529, (await b.WaitAsync(Limits.Test)).Version);
            var codes = new List<ushort>();
            for (int i = 0; i < 2; i++)
                codes.Add((await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code);
            Assert.Equal(new ushort[] { 0x0003, 0x0009 }, codes);         // the foreign frame, then A's real reply
            Assert.False(client.Unsolicited.TryRead(out _));
            Assert.Empty(logs.Events(1100));
        }
    }

    [Fact]
    public async Task ForeignReply_WithoutRealReply_ClosesAfterDeadline()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            await device.ReadRequestAsync();
            await device.SendAsync(Resilient.VersionReply);
            await Assert.ThrowsAsync<NetSdrProtocolException>(() => a.WaitAsync(Limits.Test));
            await Eventually.ThatAsync(() => logs.Events(1102).Count == 1);   // 750 ms after the write
            Assert.Equal(LogLevel.Warning, logs.Events(1102)[0].Level);
            Assert.False(device.Client.IsConnected);
            Assert.False(client.IsConnected);
        }
    }
}
