using System.Runtime.InteropServices;
using NetSdr.Items;

namespace NetSdr.Examples.Vega.Items;

/// <summary>Board temperature of firmware v1, item 0x8002: hundredths of a degree Celsius in a signed 16-bit field.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct BoardTemperatureV1 : IControlItem<BoardTemperatureV1>
{
    public static ushort Code => VegaProtocol.BoardTemperatureCode;

    public readonly TemperatureSensor Sensor;
    public readonly short CentiCelsius;

    public BoardTemperatureV1(TemperatureSensor sensor, short centiCelsius)
    {
        Sensor = sensor;
        CentiCelsius = centiCelsius;
    }

    public double Celsius => CentiCelsius / 100.0;

    public static BoardTemperatureV1 FromCelsius(TemperatureSensor sensor, double celsius) =>
        new(sensor, (short)Math.Round(celsius * 100));
}
