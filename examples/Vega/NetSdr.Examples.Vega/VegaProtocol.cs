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

    // The service protocol of the independent Vega microcontroller, alive even when the main firmware hangs. TCP, one
    // connection per request. Request, 8 bytes: 56 53 01 cmd k0 k1 k2 k3 ("VS", version 1, cmd 1 soft or 2 hard, the
    // unlock key of VendorUnlock little-endian). Reply, 4 bytes: 56 53 cmd status (0 accepted, 1 wrong key, 2 busy).
    // The device closes the connection after the reply.

    /// <summary>The TCP port of the service protocol.</summary>
    public const int ServicePort = 50001;

    /// <summary>The version byte of a service request.</summary>
    public const byte ServiceVersion = 1;

    /// <summary>The first magic byte of a service request and reply, ASCII <c>V</c>.</summary>
    public const byte ServiceMagic0 = 0x56;

    /// <summary>The second magic byte of a service request and reply, ASCII <c>S</c>.</summary>
    public const byte ServiceMagic1 = 0x53;

    /// <summary>The service command of a soft reboot.</summary>
    public const byte SoftRebootCommand = 1;

    /// <summary>The service command of a hard reboot.</summary>
    public const byte HardRebootCommand = 2;

    /// <summary>The service status: the reboot was accepted.</summary>
    public const byte ServiceAccepted = 0;

    /// <summary>The service status: the unlock key was wrong, nothing happened.</summary>
    public const byte ServiceBadKey = 1;

    /// <summary>The service status: the device is booting and did not accept the reboot.</summary>
    public const byte ServiceBusy = 2;
}
