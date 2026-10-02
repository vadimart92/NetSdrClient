namespace NetSdr.Examples.Vega;

/// <summary>Constants of the Vega receiver family: a vendor-specific extension of the NetSDR control protocol.</summary>
public static class VegaProtocol
{
    /// <summary>Product ID 0x41474556, the ASCII bytes <c>VEGA</c> in little-endian order.</summary>
    public const uint ProductId = 0x41474556;

    /// <summary>Maximum length of a <see cref="Items.DeviceLabel"/>, in characters.</summary>
    public const int MaxLabelLength = 32;

    public const ushort VendorUnlockCode = 0x8000;
    public const ushort AntennaSelectCode = 0x8001;

    /// <summary>Board temperature; firmware v1 and v2 share this code but use different payloads.</summary>
    public const ushort BoardTemperatureCode = 0x8002;

    public const ushort DeviceLabelCode = 0x8003;
    public const ushort OverloadEventCode = 0x8004;
    public const ushort FirmwareInfoCode = 0x8005;
}
