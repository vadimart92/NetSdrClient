using Microsoft.Extensions.Logging;

namespace NetSdr.Identification;

/// <summary>
/// The log events of identification, ids 1300-1399: 1300-1304 of <see cref="DeviceIdentity.ReadAsync"/> and its
/// probes (category <c>NetSdr.Identification.DeviceIdentity</c>), 1310-1312 of <see cref="DeviceCatalog{TDevice}"/>
/// (category <c>NetSdr.Identification.DeviceCatalog</c>).
/// </summary>
internal static partial class IdentificationLog
{
    /// <summary>The category of <see cref="DeviceCatalog{TDevice}"/>, without the generic argument.</summary>
    public const string CatalogCategory = "NetSdr.Identification.DeviceCatalog";

    private const string None = "none";

    [LoggerMessage(EventId = 1300, EventName = "ProbeAnswered", Level = LogLevel.Debug,
        Message = "Standard probe {Item} 0x{Code:X4} answered: {Value}")]
    public static partial void ProbeAnswered(ILogger logger, string item, ushort code, object? value);

    [LoggerMessage(EventId = 1301, EventName = "ProbeUnsupported", Level = LogLevel.Debug,
        Message = "Standard probe {Item} 0x{Code:X4} rejected with a NAK")]
    public static partial void ProbeUnsupported(ILogger logger, string item, ushort code);

    [LoggerMessage(EventId = 1302, EventName = "ProbeCompleted", Level = LogLevel.Debug,
        Message = "Probe {Index} of {Count} finished in {Duration}; new facts: {Facts}; new unsupported codes: {Unsupported}")]
    public static partial void ProbeCompleted(
        ILogger logger, int index, int count, TimeSpan duration, string facts, string unsupported);

    [LoggerMessage(EventId = 1303, EventName = "IdentificationFailed", Level = LogLevel.Debug,
        Message = "Identification failed at {Step} after {Duration}")]
    public static partial void IdentificationFailed(ILogger logger, string step, TimeSpan duration, Exception exception);

    [LoggerMessage(EventId = 1304, EventName = "IdentityRead", Level = LogLevel.Information,
        Message = "Identified {Name} ({Model}), serial {SerialNumber}, firmware {FirmwareVersion}, product {ProductId}, in {Duration}; unsupported: {Unsupported}; facts: {Facts}")]
    public static partial void IdentityRead(
        ILogger logger,
        string? name,
        KnownModel model,
        string? serialNumber,
        Version? firmwareVersion,
        uint? productId,
        TimeSpan duration,
        string unsupported,
        string facts);

    [LoggerMessage(EventId = 1310, EventName = "DeviceMatched", Level = LogLevel.Information,
        Message = "{Name} ({Model}) matched registration \"{Registration}\"")]
    public static partial void DeviceMatched(ILogger logger, string? name, KnownModel model, string registration);

    [LoggerMessage(EventId = 1311, EventName = "DeviceNotRecognized", Level = LogLevel.Warning,
        Message = "No registration matched {Name} ({Model}, product {ProductId}); candidates: {Candidates}")]
    public static partial void DeviceNotRecognized(
        ILogger logger, string? name, KnownModel model, uint? productId, string candidates);

    [LoggerMessage(EventId = 1312, EventName = "AttachFailed", Level = LogLevel.Debug,
        Message = "Identifying or creating the device failed; {ClientFate}")]
    public static partial void AttachFailed(ILogger logger, string clientFate, Exception exception);

    /// <summary>Item codes in ascending order, such as <c>0x0002, 0x0009</c>; <c>none</c> when there are none.</summary>
    internal static string Codes(IEnumerable<ushort> codes)
    {
        string[] formatted = codes.Order().Select(code => $"0x{code:X4}").ToArray();
        return formatted.Length == 0 ? None : string.Join(", ", formatted);
    }

    /// <summary>Names separated by commas; <c>none</c> when there are none.</summary>
    internal static string Names(IEnumerable<string> names)
    {
        string[] all = names.ToArray();
        return all.Length == 0 ? None : string.Join(", ", all);
    }
}
