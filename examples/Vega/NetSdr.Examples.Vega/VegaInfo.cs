namespace NetSdr.Examples.Vega;

/// <summary>
/// What <see cref="VegaProbes.Identify"/> learns about a Vega receiver; a fact in <c>DeviceIdentity</c>.
/// </summary>
/// <param name="Firmware">The firmware version: 1.0 when the device does not know item 0x8005, otherwise the version it reports.</param>
/// <param name="Unlocked">Whether the vendor unlock key was accepted.</param>
public sealed record VegaInfo(Version Firmware, bool Unlocked);
