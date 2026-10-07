using System.Net;
using NetSdr.Control;

namespace NetSdr.Examples.Vega.Tests.Items;

public class VegaRebooterTests
{
    const uint Key = 0xC0DE5EC5;

    static RebootContext Ctx(IPEndPoint? last = null, string target = "127.0.0.1:50000") => new(target, last, requested: false);

    static async Task<VegaServiceServer> ServeAsync(string replyHex)
    {
        var server = new VegaServiceServer(_ => Hex.Parse(replyHex));
        await server.StartAsync();
        return server;
    }

    static VegaRebooter Rebooter(int port, string? host = "127.0.0.1", uint key = Key) =>
        new(new VegaRebooterOptions { Host = host, Port = port, UnlockKey = key });

    /// <summary>One soft reboot against a service that answers <paramref name="reply"/>; the exception it ended with, or null.</summary>
    static async Task<Exception?> RebootAgainstAsync(string reply, RebootKind kind = RebootKind.Soft)
    {
        await using var server = await ServeAsync(reply);
        return await Record.ExceptionAsync(() => Rebooter(server.Port).RebootAsync(kind, Ctx(), default).WaitAsync(Limits.Test));
    }

    static async Task AssertRequestAsync(RebootKind kind, string reply, string expectedRequest)
    {
        await using var server = await ServeAsync(reply);
        await Rebooter(server.Port).RebootAsync(kind, Ctx(), default).WaitAsync(Limits.Test);
        Assert.Equal(Hex.Parse(expectedRequest), Assert.Single(server.Requests));
    }

    [Fact] public Task Soft_SendsExactBytes() => AssertRequestAsync(RebootKind.Soft, "56 53 01 00", "56 53 01 01 C5 5E DE C0");
    [Fact] public Task Hard_SendsExactBytes() => AssertRequestAsync(RebootKind.Hard, "56 53 02 00", "56 53 01 02 C5 5E DE C0");

    [Fact] public async Task BadKey_VegaException() => Assert.Contains("wrong unlock key", Assert.IsType<VegaException>(await RebootAgainstAsync("56 53 01 01")).Message);
    [Fact] public async Task Busy_VegaException() => Assert.Contains("busy", Assert.IsType<VegaException>(await RebootAgainstAsync("56 53 01 02")).Message);
    [Fact] public async Task OtherStatus_VegaException() => Assert.Contains("status 7", Assert.IsType<VegaException>(await RebootAgainstAsync("56 53 01 07")).Message);
    [Fact] public async Task TruncatedReply_IOException() => Assert.IsAssignableFrom<IOException>(await RebootAgainstAsync("56 53"));
    [Fact] public async Task WrongMagic_IOException() => Assert.IsType<IOException>(await RebootAgainstAsync("00 00 01 00"));
    [Fact] public async Task WrongCommand_IOException() => Assert.IsType<IOException>(await RebootAgainstAsync("56 53 02 00"));   // the command byte must echo the request

    [Fact]
    public async Task HostFromContext_UsesLastRemoteEndPoint()
    {
        await using var server = await ServeAsync("56 53 01 00");
        var context = Ctx(new IPEndPoint(IPAddress.Loopback, 50000), target: "nowhere.invalid:50000");
        await Rebooter(server.Port, host: null).RebootAsync(RebootKind.Soft, context, default).WaitAsync(Limits.Test);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task HostFromTarget_WhenNoEndPoint()
    {
        await using var server = await ServeAsync("56 53 01 00");
        await Rebooter(server.Port, host: null).RebootAsync(RebootKind.Soft, Ctx(target: "127.0.0.1:50000"), default).WaitAsync(Limits.Test);
        Assert.Single(server.Requests);
    }

    [Fact]
    public void GetBootTime_FromOptions()
    {
        var defaults = new VegaRebooter(new VegaRebooterOptions { UnlockKey = Key });
        Assert.Equal((8, 20), ((int)defaults.GetBootTime(RebootKind.Soft).TotalSeconds, (int)defaults.GetBootTime(RebootKind.Hard).TotalSeconds));
        var custom = new VegaRebooter(new VegaRebooterOptions { UnlockKey = Key, SoftBootTime = TimeSpan.FromSeconds(1), HardBootTime = TimeSpan.FromSeconds(2) });
        Assert.Equal((1, 2), ((int)custom.GetBootTime(RebootKind.Soft).TotalSeconds, (int)custom.GetBootTime(RebootKind.Hard).TotalSeconds));
        Assert.Equal((VegaProtocol.ServicePort, 50001), (new VegaRebooterOptions().Port, VegaProtocol.ServicePort));
    }

    [Theory]
    [InlineData(0, 8, 20)]
    [InlineData(65536, 8, 20)]
    [InlineData(50001, -1, 20)]
    [InlineData(50001, 8, -1)]
    public void InvalidOptions_Throw(int port, int softSeconds, int hardSeconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VegaRebooter(new VegaRebooterOptions
        {
            Port = port, SoftBootTime = TimeSpan.FromSeconds(softSeconds), HardBootTime = TimeSpan.FromSeconds(hardSeconds),
        }));
}
