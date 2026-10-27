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

    [Test]
    public async Task BusyDevice_LateReplyAdopted_NoResend()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs),
            s => s.OnRequest(RfGain.Code, Resilient.Once(ControlReply.Echo.After(Late))));
        await using (server)
        await using (client)
        {
            var local = client.LocalEndPoint;
            Assert.That((await client.SetAsync(new RfGain(0, -20)).WaitAsync(Limits.Test)).GainDb, Is.EqualTo(-20));
            Assert.That(server.Received, Has.Exactly(1).Matches<ControlRequest>(r => r.Code == RfGain.Code));
            Assert.That(logs.Events(1100).Sole().Value("Reason"), Is.EqualTo("NoReplyWaitingForLateReply"));
            Assert.That(logs.Events(1107).Sole().Value("Outcome"), Is.EqualTo("Reply"));
            Assert.That(logs.Events(1103), Is.Empty);
            Assert.That(client.LocalEndPoint, Is.EqualTo(local));
        }
    }

    [Test]
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
            Assert.That((await set.WaitAsync(Limits.Test)).GainDb, Is.EqualTo(-20));
            Assert.That((await get.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
            for (byte id = 0; id <= 3; id++)                     // as ReadFirmwareAsync asks, each answered late once
            {
                var reply = await client.SendAsync(RequestType.Get, FirmwareVersion.Code, new[] { id }).WaitAsync(Limits.Test);
                Assert.That(reply.Payload.ToArray(), Is.EqualTo(new byte[] { id, (byte)(100 + id), 0 }));
            }

            Assert.That(server.Received.Count(r => r.Code == FirmwareVersion.Code), Is.EqualTo(4));
        }
    }

    [Test]
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
            Assert.That(nak.Code, Is.EqualTo(SerialNumber.Code));
            Assert.That((await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
            var options = new IdentificationOptions { IncludeStandardProbes = false };
            options.Probes.Add(Probes.Item<SerialNumber>());
            options.Probes.Add(Probes.Item<InterfaceVersion>());
            var identity = await DeviceIdentity.ReadAsync(client, options).WaitAsync(Limits.Test);
            Assert.That(identity.Unsupported.Sole(), Is.EqualTo(SerialNumber.Code));
            Assert.That(server.Received.Count(r => r.Code == SerialNumber.Code), Is.EqualTo(2));   // one per call, none resent
        }
    }

    [Test]
    public async Task NoWriteWhileUnanswered()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            Assert.That(await device.ReadRequestAsync(), Is.EqualTo(Hex.Parse("04 20 09 00")));
            await Eventually.ThatAsync(() => logs.Events(1100).Count == 1);   // A timed out; its next attempt adopts
            var b = client.GetAsync<InterfaceVersion>();
            var next = device.ReadRequestAsync();
            await Task.Delay(300);
            Assert.That(next.IsCompleted, Is.False);
            await device.SendAsync(Resilient.ProductReply);
            Assert.That(await next.WaitAsync(Limits.Test), Is.EqualTo(Hex.Parse("04 20 03 00")));
            await device.SendAsync(Resilient.VersionReply);
            Assert.That((await a.WaitAsync(Limits.Test)).Value, Is.EqualTo(0x03524453u));
            Assert.That((await b.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
        }
    }

    [Test]
    public async Task ForeignReply_NotRetried_LineWaitsForRealReply()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            await device.ReadRequestAsync();
            await device.SendAsync(Resilient.VersionReply);                    // Response 0x0003: foreign to A
            await Assert.ThatAsync(() => a.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());
            var b = client.GetAsync<InterfaceVersion>();
            var next = device.ReadRequestAsync();
            await Task.Delay(300);
            Assert.That(next.IsCompleted, Is.False);
            await device.SendAsync(Resilient.ProductReply);                    // A's real reply frees the line
            Assert.That(await next.WaitAsync(Limits.Test), Is.EqualTo(Hex.Parse("04 20 03 00")));
            await device.SendAsync(Resilient.VersionReply);
            Assert.That((await b.WaitAsync(Limits.Test)).Version, Is.EqualTo(529));
            var codes = new List<ushort>();
            for (int i = 0; i < 2; i++)
                codes.Add((await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code);
            Assert.That(codes, Is.EqualTo(new ushort[] { 0x0003, 0x0009 }));   // the foreign frame, then A's real reply
            Assert.That(client.Unsolicited.TryRead(out _), Is.False);
            Assert.That(logs.Events(1100), Is.Empty);
        }
    }

    [Test]
    public async Task ForeignReply_WithoutRealReply_ClosesAfterDeadline()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            await device.ReadRequestAsync();
            await device.SendAsync(Resilient.VersionReply);
            await Assert.ThatAsync(() => a.WaitAsync(Limits.Test), Throws.TypeOf<NetSdrProtocolException>());
            await Eventually.ThatAsync(() => logs.Events(1102).Count == 1);   // 750 ms after the write
            Assert.That(logs.Events(1102)[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(device.Client.IsConnected, Is.False);
            Assert.That(client.IsConnected, Is.False);
        }
    }
}
