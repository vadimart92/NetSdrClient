namespace NetSdr.Control;

/// <summary>How hard an <see cref="IDeviceRebooter"/> reboots the device.</summary>
public enum RebootKind
{
    /// <summary>A reboot the device performs itself, such as a firmware restart.</summary>
    Soft,

    /// <summary>A reboot that does not rely on the device's software, such as a power cycle.</summary>
    Hard,
}
