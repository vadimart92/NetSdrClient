using System.Net;
using NetSdr.Control;

namespace NetSdr.Examples.Vega.Tests.Items;

public class VegaRebooterTests
{
    private const uint Key = 0xC0DE5EC5;

    private static RebootContext Ctx(IPEndPoint? last = null, string target = "127.0.0.1:50000") => new(target, last, requested: false);

    private static async Task<VegaServiceServer> ServeAsync(string replyHex)
    {
        var server = new VegaServiceServer(_ => Hex.Parse(replyHex));
        await server.StartAsync();
        return server;
    }

    private static VegaRebooter Rebooter(int port, string? host = "127.0.0.1", uint key = Key) =>
        new(new VegaRebooterOptions { Host = host, Port = port, UnlockKey = key });

    /// <summary>One soft reboot against a service that answers <paramref name="reply"/>; the exception it ended with, or null.</summary>
    private static async Task<Exception?> RebootAgainstAsync(string reply, RebootKind kind = RebootKind.Soft)
    {
        await using var server = await ServeAsync(reply);
        try
        {
            await Rebooter(server.Port).RebootAsync(kind, Ctx(), default).WaitAsync(Limits.Test);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task AssertRequestAsync(RebootKind kind, string reply, string expectedRequest)
    {
        await using var server = await ServeAsync(reply);
        await Rebooter(server.Port).RebootAsync(kind, Ctx(), default).WaitAsync(Limits.Test);
        Assert.That(server.Requests.Sole(), Is.EqualTo(Hex.Parse(expectedRequest)));
    }

    [Test] public Task Soft_SendsExactBytes() => AssertRequestAsync(RebootKind.Soft, "56 53 01 00", "56 53 01 01 C5 5E DE C0");
    [Test] public Task Hard_SendsExactBytes() => AssertRequestAsync(RebootKind.Hard, "56 53 02 00", "56 53 01 02 C5 5E DE C0");

    [Test] public async Task BadKey_VegaException() => Assert.That(await RebootAgainstAsync("56 53 01 01"), Is.TypeOf<VegaException>().With.Message.Contains("wrong unlock key"));
    [Test] public async Task Busy_VegaException() => Assert.That(await RebootAgainstAsync("56 53 01 02"), Is.TypeOf<VegaException>().With.Message.Contains("busy"));
    [Test] public async Task OtherStatus_VegaException() => Assert.That(await RebootAgainstAsync("56 53 01 07"), Is.TypeOf<VegaException>().With.Message.Contains("status 7"));
    [Test] public async Task TruncatedReply_IOException() => Assert.That(await RebootAgainstAsync("56 53"), Is.InstanceOf<IOException>());
    [Test] public async Task WrongMagic_IOException() => Assert.That(await RebootAgainstAsync("00 00 01 00"), Is.TypeOf<IOException>());
    [Test] public async Task WrongCommand_IOException() => Assert.That(await RebootAgainstAsync("56 53 02 00"), Is.TypeOf<IOException>());   // the command byte must echo the request

    [Test]
    public async Task HostFromContext_UsesLastRemoteEndPoint()
    {
        await using var server = await ServeAsync("56 53 01 00");
        var context = Ctx(new IPEndPoint(IPAddress.Loopback, 50000), target: "nowhere.invalid:50000");
        await Rebooter(server.Port, host: null).RebootAsync(RebootKind.Soft, context, default).WaitAsync(Limits.Test);
        Assert.That(server.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task HostFromTarget_WhenNoEndPoint()
    {
        await using var server = await ServeAsync("56 53 01 00");
        await Rebooter(server.Port, host: null).RebootAsync(RebootKind.Soft, Ctx(target: "127.0.0.1:50000"), default).WaitAsync(Limits.Test);
        Assert.That(server.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public void GetBootTime_FromOptions()
    {
        var defaults = new VegaRebooter(new VegaRebooterOptions { UnlockKey = Key });
        Assert.That(
            ((int)defaults.GetBootTime(RebootKind.Soft).TotalSeconds, (int)defaults.GetBootTime(RebootKind.Hard).TotalSeconds),
            Is.EqualTo((8, 20)));
        var custom = new VegaRebooter(new VegaRebooterOptions { UnlockKey = Key, SoftBootTime = TimeSpan.FromSeconds(1), HardBootTime = TimeSpan.FromSeconds(2) });
        Assert.That(
            ((int)custom.GetBootTime(RebootKind.Soft).TotalSeconds, (int)custom.GetBootTime(RebootKind.Hard).TotalSeconds),
            Is.EqualTo((1, 2)));
        Assert.That((new VegaRebooterOptions().Port, VegaProtocol.ServicePort), Is.EqualTo((VegaProtocol.ServicePort, 50001)));
    }

    [TestCase(0, 8, 20)]
    [TestCase(65536, 8, 20)]
    [TestCase(50001, -1, 20)]
    [TestCase(50001, 8, -1)]
    public void InvalidOptions_Throw(int port, int softSeconds, int hardSeconds)
    {
        var options = new VegaRebooterOptions
        {
            Port = port,
            SoftBootTime = TimeSpan.FromSeconds(softSeconds),
            HardBootTime = TimeSpan.FromSeconds(hardSeconds),
        };
        Assert.That(() => new VegaRebooter(options), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
