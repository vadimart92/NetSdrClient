using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class InterfaceParityTests
{
    private static async Task<(NetSdrTestServer Server, INetSdrControlClient Client)> StartAsync(bool resilient, Action<NetSdrTestServer>? setup = null)
    {
        if (resilient)
        {
            var (s, c) = await Resilient.StartAsync(Resilient.Fast(), setup);
            return (s, c);
        }

        var (server, client) = await Loopback.StartAsync(setup);
        return (server, client);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InterfaceParity_Commands(bool resilient)
    {
        var (server, client) = await StartAsync(resilient, s =>
        {
            s.Preload(new InterfaceVersion(529));
            s.Preload(new FirmwareVersion(1, 120));
            s.OnRequest(RfGain.Code, r => r.Type == RequestType.GetRange ? ControlReply.Bytes(new byte[] { 0, 0xEC }) : ControlReply.Echo);
        });
        await using (server)
        await using (client)
        {
            Assert.That((await client.SetAsync(new RfGain(0, -20))).GainDb, Is.EqualTo(-20));
            Assert.That((await client.GetAsync<InterfaceVersion>()).Version, Is.EqualTo(529));
            Assert.That((await client.GetAsync<FirmwareVersion, byte>(1)).Version, Is.EqualTo(120));
            Assert.That((await client.GetRangeAsync<RfGain, byte>(0)).GainDb, Is.EqualTo(-20));
            var raw = await client.SendAsync(RequestType.Get, InterfaceVersion.Code, ReadOnlyMemory<byte>.Empty);
            Assert.That((raw.Type, Convert.ToHexString(raw.Payload.Span)), Is.EqualTo((ReplyType.Response, "1102")));
            Assert.That(Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>()).Code, Is.EqualTo(ProductId.Code));
            await server.SendUnsolicitedAsync(new AfGain(0, 9));
            ControlItemMessage pushed;
            do
                pushed = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
            while (pushed.Type != ReplyType.Unsolicited);
            Assert.That(pushed.Code, Is.EqualTo(AfGain.Code));
            Assert.That(client.IsConnected, Is.True);
            Assert.That(client.RemoteEndPoint!.Port, Is.EqualTo(server.Port));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InterfaceParity_InvalidArguments_ThrowSynchronously_NothingSent(bool resilient)
    {
        var (server, client) = await StartAsync(resilient);
        await using (server)
        await using (client)
        {
            var oversize = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = client.SendAsync(RequestType.Set, 0x7000, new byte[NetSdrControlClient.MaxPayloadSize + 1]); });
            Assert.That(oversize.Message, Does.Contain($"the limit is {NetSdrControlClient.MaxPayloadSize} bytes"));
            Assert.That(
                () => { _ = client.SendAsync(RequestType.Data0, 0x7000, ReadOnlyMemory<byte>.Empty); },
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => { _ = client.SetAsync(new StatusCodes(new[] { StatusCodes.Idle })); }, Throws.TypeOf<ArgumentException>());
            await Task.Delay(100);
            Assert.That(server.Received, Has.None.Matches<ControlRequest>(r => r.Code == 0x7000 || r.Type == RequestType.Set));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InterfaceParity_IdentificationAndCatalog(bool resilient)
    {
        var (server, client) = await StartAsync(resilient, s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            var identity = await DeviceIdentity.ReadAsync(client);
            Assert.That(identity.InterfaceVersion, Is.EqualTo(new Version(5, 29)));
            Assert.That(identity.Unsupported, Has.Count.EqualTo(5));
            var device = await new DeviceCatalog<DeviceIdentity>().Default((c, id) => id).AttachAsync(client);
            Assert.That(device.InterfaceVersion, Is.EqualTo(identity.InterfaceVersion));
        }
    }
}
