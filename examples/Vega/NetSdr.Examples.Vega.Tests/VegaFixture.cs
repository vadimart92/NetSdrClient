using System.Net;
using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests;

/// <summary>A Vega emulator and the client connected to it over loopback.</summary>
internal static class VegaFixture
{
    public static async Task<(VegaEmulator Emulator, VegaReceiverBase Vega)> ConnectAsync(
        VegaFirmware firmware = VegaFirmware.V2)
    {
        var emulator = new VegaEmulator(firmware: firmware);
        try
        {
            await emulator.StartAsync();
            using var timeout = new CancellationTokenSource(Limits.Test);
            var vega = await VegaReceiverBase.ConnectAsync(
                "127.0.0.1", emulator.Port, VegaEmulator.DefaultKey, ct: timeout.Token);
            return (emulator, vega);
        }
        catch
        {
            await emulator.DisposeAsync();
            throw;
        }
    }

    // The server serves one client at a time, so a NAK for a fresh client proves the previous one is gone.
    public static async Task AssertServerFreeAsync(NetSdrTestServer server)
    {
        await using var next = new NetSdrControlClient();
        await next.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        await Assert.ThrowsAsync<NetSdrNakException>(
            () => next.SendAsync(RequestType.Get, 0x7FFF, ReadOnlyMemory<byte>.Empty).WaitAsync(Limits.Test));
    }
}
