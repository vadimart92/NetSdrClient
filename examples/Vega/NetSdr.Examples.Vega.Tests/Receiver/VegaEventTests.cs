using NetSdr.Examples.Vega.Items;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests.Receiver;

public class VegaEventTests
{
    private static async Task<List<VegaEvent>> TakeAsync(VegaReceiverBase vega, int count)
    {
        using var cts = new CancellationTokenSource(Limits.Test);
        var events = new List<VegaEvent>();
        await foreach (var e in vega.ReadEventsAsync(cts.Token))
        {
            events.Add(e);
            if (events.Count == count)
                break;
        }
        return events;
    }

    [TestCase(VegaFirmware.V1)]
    [TestCase(VegaFirmware.V2)]
    public async Task Events_ParsedPerFirmware_InOrder(VegaFirmware firmware)
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync(firmware);
        await using var _ = emulator;
        await using var __ = vega;
        await emulator.SendTemperatureAsync(TemperatureSensor.Board, 36.6);
        await emulator.SendOverloadAsync(1, OverloadedStages.Rf);
        byte? status = firmware == VegaFirmware.V2 ? (byte)0 : null;
        Assert.That(
            await TakeAsync(vega, 2),
            Is.EqualTo(new VegaEvent[] { new TemperatureReport(TemperatureSensor.Board, 36.6, status), new OverloadDetected(1, OverloadedStages.Rf) }));
    }

    [Test]
    public async Task EventDuringActiveRequest_DoesNotBreakIt()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        emulator.Server.OnRequest<BoardTemperatureV2>(r =>
        {
            emulator.SendOverloadAsync(0, OverloadedStages.Adc).GetAwaiter().GetResult();
            return ControlReply.Item(BoardTemperatureV2.FromCelsius(r.Key<TemperatureSensor>(), 30));
        });
        Assert.That(await vega.GetTemperatureAsync(TemperatureSensor.Board), Is.EqualTo(30.0));
        Assert.That(await TakeAsync(vega, 1), Is.EqualTo(new VegaEvent[] { new OverloadDetected(0, OverloadedStages.Adc) }));
    }

    [Test]
    public async Task MalformedAndUnknown_AreSkipped()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator;
        await using var __ = vega;
        await emulator.Server.SendUnsolicitedAsync(VegaProtocol.BoardTemperatureCode, new byte[] { 1 });
        await emulator.Server.SendUnsolicitedAsync(0x9999, new byte[] { 1, 2 });
        await emulator.SendOverloadAsync(2, OverloadedStages.Adc);
        Assert.That(await TakeAsync(vega, 1), Is.EqualTo(new VegaEvent[] { new OverloadDetected(2, OverloadedStages.Adc) }));
    }
}
