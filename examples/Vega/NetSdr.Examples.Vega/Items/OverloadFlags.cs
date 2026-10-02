namespace NetSdr.Examples.Vega.Items;

/// <summary>Which stages of a channel are overloaded.</summary>
[Flags]
public enum OverloadFlags : byte
{
    None = 0,
    Adc = 1,
    Rf = 2,
}
