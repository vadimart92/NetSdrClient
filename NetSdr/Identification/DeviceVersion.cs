namespace NetSdr.Identification;

/// <summary>Conversions of version numbers as the device reports them.</summary>
public static class DeviceVersion
{
    /// <summary>Converts a version in hundredths to a <see cref="Version"/>: 529 becomes 5.29.</summary>
    public static Version FromHundredths(ushort value) => new(value / 100, value % 100);
}
