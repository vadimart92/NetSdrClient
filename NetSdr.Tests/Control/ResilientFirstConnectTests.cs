using System.Net;
using System.Net.Sockets;
using NetSdr.Control;

namespace NetSdr.Tests.Control;

public class ResilientFirstConnectTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidConnectAttempts_Throws(int attempts)
    {
        var options = new ResilientControlClientOptions { ConnectAttempts = attempts };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), options); });
        Assert.Contains("ConnectAttempts must be at least 1.", ex.Message);
    }

    [Fact]
    public async Task RecoveryPolicyWithoutRebooter_IsAllowed()
    {
        var options = new ResilientControlClientOptions { RecoveryPolicy = new EscalatingRecoveryPolicy() };   // validated, never called
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThrowsAsync<SocketException>(() => ResilientControlClient.ConnectAsync(refused.ConnectAsync, "pipe", options, default).WaitAsync(Limits.Test));
    }
}
