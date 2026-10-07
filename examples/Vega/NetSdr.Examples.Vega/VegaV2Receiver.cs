using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Identification;

namespace NetSdr.Examples.Vega;

/// <summary>The client of a Vega receiver with firmware v2, whose board temperature is a <see cref="BoardTemperatureV2"/>.</summary>
public sealed class VegaV2Receiver : VegaReceiverBase
{
    /// <inheritdoc cref="VegaReceiverBase(INetSdrControlClient, DeviceIdentity)"/>
    public VegaV2Receiver(INetSdrControlClient control, DeviceIdentity identity)
        : base(control, identity)
    {
    }

    public override async Task<double> GetTemperatureAsync(TemperatureSensor sensor, CancellationToken ct = default)
    {
        var reply = await Control.GetAsync<BoardTemperatureV2, TemperatureSensor>(sensor, ct).ConfigureAwait(false);
        return reply.Celsius;
    }

    protected override VegaEvent? TryParseEvent(in ControlItemMessage message)
    {
        if (!message.Is<BoardTemperatureV2>())
        {
            return null;
        }

        var temperature = message.As<BoardTemperatureV2>();
        return new TemperatureReport(temperature.Sensor, temperature.Celsius, temperature.Status);
    }
}
