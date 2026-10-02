namespace NetSdr.Examples.Vega.Items;

/// <summary>The sensor a board temperature reading comes from.</summary>
public enum TemperatureSensor : byte
{
    Board = 0,
    Adc = 1,
    Fpga = 2,
}
