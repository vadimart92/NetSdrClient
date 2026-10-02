namespace NetSdr.Identification;

/// <summary>
/// Collects what identification learns about a device and hands it out as <see cref="DeviceIdentity"/> snapshots.
/// The standard probes fill the standard fields; a probe of the application adds facts with <see cref="Set{TFact}"/>
/// and reads what is there with <see cref="Current"/>. A builder can also be created directly, to test a probe
/// without a device. It is not thread-safe: probes run one after another.
/// </summary>
public sealed class DeviceIdentityBuilder
{
    private readonly Dictionary<Type, object> _facts = [];
    private readonly HashSet<ushort> _unsupported = [];

    /// <summary>
    /// The identity as collected up to now. Every call returns a new snapshot that later changes of the builder
    /// do not touch.
    /// </summary>
    public DeviceIdentity Current => new(this);

    /// <summary>Stores <paramref name="fact"/> under the type <typeparamref name="TFact"/>, replacing an earlier fact of that type.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="fact"/> is <see langword="null"/>.</exception>
    public void Set<TFact>(TFact fact) where TFact : notnull
    {
        if (fact is null)
        {
            throw new ArgumentNullException(nameof(fact));
        }

        _facts[typeof(TFact)] = fact;
    }

    /// <summary>Records that the device answered the request for the control item <paramref name="code"/> with a NAK.</summary>
    public void MarkUnsupported(ushort code) => _unsupported.Add(code);

    internal string? Name { get; set; }

    internal string? SerialNumber { get; set; }

    internal Version? InterfaceVersion { get; set; }

    internal Version? BootVersion { get; set; }

    internal Version? FirmwareVersion { get; set; }

    internal Version? HardwareVersion { get; set; }

    internal FpgaInfo? Fpga { get; set; }

    internal uint? ProductId { get; set; }

    internal NetSdr.Items.Options? Options { get; set; }

    internal IReadOnlyDictionary<Type, object> Facts => _facts;

    internal IReadOnlySet<ushort> UnsupportedCodes => _unsupported;
}
