namespace NetSdr.Identification;

/// <summary>The RFSPACE product family a device reports in its target name (item 0x0001).</summary>
public enum KnownModel
{
    /// <summary>The name is missing or not one of the known families.</summary>
    Unknown = 0,
    SdrIp,
    NetSdr,
    CloudIq,
    CloudSdr,
}

internal static class KnownModelNames
{
    /// <summary>Maps a target name to its family by case-insensitive prefix; anything else, including <see langword="null"/>, is <see cref="KnownModel.Unknown"/>.</summary>
    internal static KnownModel FromName(string? name)
    {
        if (name is null)
        {
            return KnownModel.Unknown;
        }

        if (name.StartsWith("SDR-IP", StringComparison.OrdinalIgnoreCase))
        {
            return KnownModel.SdrIp;
        }

        if (name.StartsWith("NetSDR", StringComparison.OrdinalIgnoreCase))
        {
            return KnownModel.NetSdr;
        }

        if (name.StartsWith("CloudIQ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Cloud-IQ", StringComparison.OrdinalIgnoreCase))
        {
            return KnownModel.CloudIq;
        }

        if (name.StartsWith("CloudSDR", StringComparison.OrdinalIgnoreCase))
        {
            return KnownModel.CloudSdr;
        }

        return KnownModel.Unknown;
    }
}
