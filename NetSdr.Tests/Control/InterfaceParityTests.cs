using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Identification;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class InterfaceParityTests
{
    static async Task<(NetSdrTestServer Server, INetSdrControlClient Client)> StartAsync(bool resilient, Action<NetSdrTestServer>? setup = null)
    {
        if (resilient)
        {
            var (s, c) = await Resilient.StartAsync(Resilient.Fast(), setup);
            return (s, c);
        }

        var (server, client) = await Loopback.StartAsync(setup);
        return (server, client);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
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
            Assert.Equal(-20, (await client.SetAsync(new RfGain(0, -20))).GainDb);
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
            Assert.Equal(120, (await client.GetAsync<FirmwareVersion, byte>(1)).Version);
            Assert.Equal(-20, (await client.GetRangeAsync<RfGain, byte>(0)).GainDb);
            var raw = await client.SendAsync(RequestType.Get, InterfaceVersion.Code, ReadOnlyMemory<byte>.Empty);
            Assert.Equal((ReplyType.Response, "1102"), (raw.Type, Convert.ToHexString(raw.Payload.Span)));
            Assert.Equal(ProductId.Code, (await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>())).Code);
            await server.SendUnsolicitedAsync(new AfGain(0, 9));
            ControlItemMessage pushed;
            do pushed = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
            while (pushed.Type != ReplyType.Unsolicited);
            Assert.Equal(AfGain.Code, pushed.Code);
            Assert.True(client.IsConnected);
            Assert.Equal(server.Port, client.RemoteEndPoint!.Port);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterfaceParity_InvalidArguments_ThrowSynchronously_NothingSent(bool resilient)
    {
        var (server, client) = await StartAsync(resilient);
        await using (server)
        await using (client)
        {
            var oversize = Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = client.SendAsync(RequestType.Set, 0x7000, new byte[NetSdrControlClient.MaxPayloadSize + 1]); });
            Assert.Contains($"the limit is {NetSdrControlClient.MaxPayloadSize} bytes", oversize.Message);
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = client.SendAsync(RequestType.Data0, 0x7000, ReadOnlyMemory<byte>.Empty); });
            Assert.Throws<ArgumentException>(() => { _ = client.SetAsync(new StatusCodes(new[] { StatusCodes.Idle })); });
            await Task.Delay(100);
            Assert.DoesNotContain(server.Received, r => r.Code == 0x7000 || r.Type == RequestType.Set);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterfaceParity_IdentificationAndCatalog(bool resilient)
    {
        var (server, client) = await StartAsync(resilient, s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            var identity = await DeviceIdentity.ReadAsync(client);
            Assert.Equal(new Version(5, 29), identity.InterfaceVersion);
            Assert.Equal(5, identity.Unsupported.Count);
            var device = await new DeviceCatalog<DeviceIdentity>().Default((c, id) => id).AttachAsync(client);
            Assert.Equal(identity.InterfaceVersion, device.InterfaceVersion);
        }
    }
}
