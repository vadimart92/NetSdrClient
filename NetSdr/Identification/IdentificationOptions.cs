using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetSdr.Control;

namespace NetSdr.Identification;

/// <summary>
/// A probe: a step of identification that talks to the device through <paramref name="client"/> and records what it
/// learns in <paramref name="builder"/>. It may read what earlier probes collected from <see cref="DeviceIdentityBuilder.Current"/>.
/// </summary>
/// <param name="client">The connected client; requests go through it one at a time, with its response timeout.</param>
/// <param name="builder">The identity collected so far.</param>
/// <param name="ct">Cancels the identification.</param>
public delegate Task ProbeAsync(INetSdrControlClient client, DeviceIdentityBuilder builder, CancellationToken ct);

/// <summary>Settings of <see cref="DeviceIdentity.ReadAsync"/>.</summary>
public sealed class IdentificationOptions
{
    /// <summary>Whether the standard items 0x0001 to 0x000A are read before <see cref="Probes"/>. On by default.</summary>
    public bool IncludeStandardProbes { get; set; } = true;

    /// <summary>The application's own probes. They run in list order, after the standard probes.</summary>
    public IList<ProbeAsync> Probes { get; } = new List<ProbeAsync>();

    /// <summary>
    /// Creates the loggers of identification: category <c>NetSdr.Identification.DeviceIdentity</c> for
    /// <see cref="DeviceIdentity.ReadAsync"/> and its probes, <c>NetSdr.Identification.DeviceCatalog</c> for a
    /// <see cref="DeviceCatalog{TDevice}"/> these options are given to. Defaults to
    /// <see cref="NullLoggerFactory.Instance"/>, which logs nothing; <see langword="null"/> is rejected by
    /// <see cref="DeviceIdentity.ReadAsync"/> and by the constructor of <see cref="DeviceCatalog{TDevice}"/>.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;
}
