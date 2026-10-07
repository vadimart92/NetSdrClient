using Microsoft.Extensions.Time.Testing;
using NetSdr.Control;
using NetSdr.Items;

namespace NetSdr.Tests.Control;

public class InnerClientHookTests
{
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
}
