using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>
/// Board temperature of firmware v2, item 0x8002 (the same code as <see cref="BoardTemperatureV1"/>):
/// thousandths of a degree Celsius in a signed 32-bit field, followed by a status byte.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct BoardTemperatureV2 : IControlItem<BoardTemperatureV2>
{
    public static ushort Code => VegaProtocol.BoardTemperatureCode;

    public readonly TemperatureSensor Sensor;
    public readonly int MilliCelsius;
    public readonly byte Status;

    public BoardTemperatureV2(TemperatureSensor sensor, int milliCelsius, byte status)
    {
        Sensor = sensor;
        MilliCelsius = milliCelsius;
        Status = status;
    }

    public double Celsius => MilliCelsius / 1000.0;

    public static BoardTemperatureV2 FromCelsius(TemperatureSensor sensor, double celsius, byte status = 0) =>
        new(sensor, (int)Math.Round(celsius * 1000), status);
}
