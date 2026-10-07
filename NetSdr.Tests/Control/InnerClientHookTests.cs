using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

public class InnerClientHookTests
{
    private const string ProductReply = "08 00 09 00 53 44 52 03";
    private const string VersionReply = "06 00 03 00 11 02";

    [Fact]
    public async Task InnerTimeProvider_DrivesResponseTimeout()
    {
        var time = new FakeTimeProvider();
        await using var device = PipeDevice.Create(new NetSdrControlClientOptions
        {
            ResponseTimeout = TimeSpan.FromSeconds(2), FaultOnTimeout = false, TimeProvider = time,
        });
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Task.Delay(100);                                 // the wait has started after the write
        time.Advance(TimeSpan.FromSeconds(1.9));
        await Task.Delay(100);
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(0.2));
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task LateReplyObserver_FiresForLateReplyAndForNakWithSlotSet()
    {
        var calls = new ConcurrentQueue<(ControlItemMessage Message, bool IsNak)>();
        var client = Inner();
        client.LateReplyObserver = (m, nak) => calls.Enqueue((m, nak));
        await using var device = PipeDevice.Attach(client);
        await AbandonAsync<ProductId>(device);
        await device.SendAsync(ProductReply);                       // frees the abandoned slot
        await Eventually.ThatAsync(() => calls.Count == 1);
        await AbandonAsync<InterfaceVersion>(device);
        await device.SendAsync("02 00");                            // NAK, nothing in flight, slot set
        await Eventually.ThatAsync(() => calls.Count == 2);
        var (late, nak) = (calls.ElementAt(0), calls.ElementAt(1));
        Assert.Equal((ReplyType.Response, (ushort)0x0009, false), (late.Message.Type, late.Message.Code, late.IsNak));
        Assert.Equal((ReplyType.Response, (ushort)0, 0, true), (nak.Message.Type, nak.Message.Code, nak.Message.Payload.Length, nak.IsNak));
    }

    [Fact]
    public async Task LateReplyObserver_SilentOtherwise()
    {
        int calls = 0;
        var client = Inner();
        client.LateReplyObserver = (_, _) => Interlocked.Increment(ref calls);
        await using var device = PipeDevice.Attach(client);
        var normal = client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync(VersionReply);
        await normal.WaitAsync(Limits.Test);
        await device.SendAsync("06 20 48 00 00 09");               // unsolicited
        await device.SendAsync("02 00");                            // NAK with an empty slot
        for (int i = 0; i < 2; i++) await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var rejected = client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("02 00");                            // NAK that answers the request
        await Assert.ThrowsAsync<NetSdrNakException>(() => rejected.WaitAsync(Limits.Test));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LateReplyObserver_RunsBeforeUnsolicited()
    {
        bool? visibleAtCall = null;
        var client = Inner();
        client.LateReplyObserver = (_, _) => visibleAtCall = client.Unsolicited.TryPeek(out _);
        await using var device = PipeDevice.Attach(client);
        await AbandonAsync<ProductId>(device);
        await device.SendAsync(ProductReply);
        Assert.Equal((ushort)0x0009, (await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code);
        Assert.False(visibleAtCall);
    }

    [Fact]
    public async Task LateReplyObserver_Unset_SameBytes() =>
        Assert.Equal(await ScriptAsync(observe: false), await ScriptAsync(observe: true));

    private static NetSdrControlClient Inner() =>
        new(new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150), FaultOnTimeout = false });

    private static async Task AbandonAsync<T>(PipeDevice device) where T : struct, IControlItem<T>
    {
        var call = device.Client.GetAsync<T>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
    }

    // A late reply, a NAK and an unsolicited frame, then the next request: every frame and message as text.
    private static async Task<string[]> ScriptAsync(bool observe)
    {
        var client = Inner();
        if (observe) client.LateReplyObserver = (_, _) => { };
        await using var device = PipeDevice.Attach(client);
        await AbandonAsync<ProductId>(device);
        await device.SendAsync(ProductReply + " 02 00 06 20 48 00 00 09");
        var seen = new List<string>();
        for (int i = 0; i < 3; i++)   // all three handled before the next request exists, so the NAK cannot reject it
        {
            var m = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
            seen.Add($"{m.Type} {m.Code:X4} {Convert.ToHexString(m.Payload.Span)}");
        }

        var next = client.GetAsync<InterfaceVersion>();
        seen.Add(Convert.ToHexString(await device.ReadRequestAsync()));
        await device.SendAsync(VersionReply);
        seen.Add((await next.WaitAsync(Limits.Test)).Version.ToString());
        return seen.ToArray();
    }
}
