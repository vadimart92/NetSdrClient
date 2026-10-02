using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Identification;

namespace NetSdr.Examples.Vega;

/// <summary>The client of a Vega receiver with firmware v1, whose board temperature is a <see cref="BoardTemperatureV1"/>.</summary>
public sealed class VegaV1Receiver : VegaReceiverBase
{
    /// <inheritdoc cref="VegaReceiverBase(NetSdrControlClient, DeviceIdentity)"/>
    public VegaV1Receiver(NetSdrControlClient control, DeviceIdentity identity)
        : base(control, identity)
    {
    }

    public override async Task<double> GetTemperatureAsync(TemperatureSensor sensor, CancellationToken ct = default)
    {
        var reply = await Control.GetAsync<BoardTemperatureV1, TemperatureSensor>(sensor, ct).ConfigureAwait(false);
        return reply.Celsius;
    }

    protected override VegaEvent? TryParseEvent(in ControlItemMessage message)
    {
        if (!message.Is<BoardTemperatureV1>())
        {
            return null;
        }

        var temperature = message.As<BoardTemperatureV1>();
        return new TemperatureReport(temperature.Sensor, temperature.Celsius);
    }
}
