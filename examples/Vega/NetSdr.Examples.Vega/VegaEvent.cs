using NetSdr.Examples.Vega.Items;

namespace NetSdr.Examples.Vega;

/// <summary>An event a Vega receiver sends on its own, without a request.</summary>
public abstract record VegaEvent;

/// <summary>A temperature measurement.</summary>
/// <param name="Sensor">The sensor that was read.</param>
/// <param name="Celsius">The temperature in degrees Celsius.</param>
/// <param name="Status">The status byte of firmware v2, which the example does not interpret; <see langword="null"/> for v1, which has none.</param>
public sealed record TemperatureReport(TemperatureSensor Sensor, double Celsius, byte? Status = null) : VegaEvent;

/// <summary>A channel is overloaded.</summary>
public sealed record OverloadDetected(byte Channel, OverloadFlags Flags) : VegaEvent;
