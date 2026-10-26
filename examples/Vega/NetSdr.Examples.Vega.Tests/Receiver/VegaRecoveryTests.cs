using System.Net;
using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaRecoveryTests
{
    // No FakeLogger here: the Vega test project has no logging test package, so the log events of these paths are pinned
    // by ResilientRecoveryTests and ResilientRebootTests; these tests check the emulator and the client state, and
    // WrongServiceKey_RebootFails_1115 records its 1115 through a small RecordingLoggerFactory.
    private static ResilientControlClientOptions Recovering(VegaEmulator emulator, uint serviceKey = VegaEmulator.DefaultKey) => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(100),
        LateReplyTimeout = TimeSpan.FromMilliseconds(200),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        RebootTimeout = TimeSpan.FromSeconds(2),
        Rebooter = new VegaRebooter(new VegaRebooterOptions
        {
            Port = emulator.ServicePort,
            UnlockKey = serviceKey,
            SoftBootTime = TimeSpan.FromMilliseconds(300),
            HardBootTime = TimeSpan.FromMilliseconds(500),   // longer than the emulator's 200 / 400 ms
        }),
        RecoveryPolicy = new EscalatingRecoveryPolicy { SoftRebootAfter = 1, HardRebootAfter = 1 },
        ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey), ct),
    };

    private static async Task<ResilientControlClient> ConnectAsync(VegaEmulator emulator, ResilientControlClientOptions options)
    {
        var client = await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), options).WaitAsync(Limits.Test);
        await client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey)).WaitAsync(Limits.Test);
        return client;
    }

    [Test]
    public async Task FirmwareHang_SoftRebootRecovers_UnlockRestored()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var client = await ConnectAsync(emulator, Recovering(emulator));
        emulator.Hang(VegaHang.Firmware);
        await Eventually.ThatAsync(() => emulator.RebootRequests.Count == 1);           // the hang was found and escalated
        await Eventually.ThatAsync(() => client.IsConnected);
        Assert.That(emulator.RebootRequests, Is.EqualTo(new[] { (RebootKind.Soft, true) }));
        Assert.That(
            (await client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test)).Value,
            Is.EqualTo(""));   // served only when unlocked: the callback ran
    }

    [Test]
    public async Task BoardHang_SoftThenHard()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var client = await ConnectAsync(emulator, Recovering(emulator));
        emulator.Hang(VegaHang.Board);
        await Eventually.ThatAsync(() => emulator.RebootRequests.Count == 2);
        await Eventually.ThatAsync(() => client.IsConnected);
        Assert.That(emulator.RebootRequests, Is.EqualTo(new[] { (RebootKind.Soft, true), (RebootKind.Hard, true) }));
        Assert.That((await client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test)).Value, Is.EqualTo(""));
    }

    [Test]
    public async Task ManualHardReboot_ClearsDeviceState()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var client = await ConnectAsync(emulator, Recovering(emulator));
        await client.SetAsync(new DeviceLabel("night shift")).WaitAsync(Limits.Test);
        await client.RebootAsync(RebootKind.Hard).WaitAsync(Limits.Test);
        Assert.That(emulator.RebootRequests, Is.EqualTo(new[] { (RebootKind.Hard, true) }));
        Assert.That((await client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test)).Value, Is.EqualTo(""));
        Assert.That(
            (await client.GetAsync<ProductId>().WaitAsync(Limits.Test)).Value,
            Is.EqualTo(VegaProtocol.ProductId));   // preloaded again
    }

    [Test]
    public async Task WrongServiceKey_RebootFails_1115()
    {
        // The failure reaches the caller as the VegaException of the service, and 1115 carries it (Ruling C22).
        var logs = new RecordingLoggerFactory();
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        var options = Recovering(emulator, serviceKey: 0x0BAD_0BAD);
        options.LoggerFactory = logs;
        await using var client = await ConnectAsync(emulator, options);
        var ex = Assert.ThrowsAsync<VegaException>(() => client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test));
        Assert.That(ex.Message, Does.Contain("wrong unlock key"));
        Assert.That(emulator.RebootRequests, Is.EqualTo(new[] { (RebootKind.Soft, false) }));
        await Eventually.ThatAsync(() => client.IsConnected);                            // the requested loss is reconnected as any other
        var failed = logs.Entries.Where(entry => entry.EventId == 1115).Sole();
        Assert.That(failed.Exception, Is.SameAs(ex));
    }

    [Test]
    public async Task HungAtStartup_FirstConnectRecoversBySoftReboot()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        emulator.Hang(VegaHang.Firmware);
        await using var client = await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), Recovering(emulator)).WaitAsync(Limits.Test);
        Assert.That(client.IsConnected, Is.True);
        Assert.That(emulator.RebootRequests, Is.EqualTo(new[] { (RebootKind.Soft, true) }));
        await Assert.ThatAsync(
            () => client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test),
            Throws.TypeOf<NetSdrNakException>());   // locked: ConnectionRestored did not run
    }
}
