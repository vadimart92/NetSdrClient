namespace NetSdr.Items;

/// <summary>One receiver frequency band: its limits and the VCO (downconverter local oscillator) frequency, all in hertz.</summary>
public readonly record struct FrequencyRange(ulong Min, ulong Max, ulong Vco);
