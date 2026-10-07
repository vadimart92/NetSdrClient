using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using NetSdr.Control;

namespace NetSdr.Identification;

/// <summary>
/// What is known about a device: the standard items 0x0001 to 0x000A, plus facts that probes of the application
/// collected. A snapshot never changes. A field the device does not have, or has not answered with a value, is
/// <see langword="null"/>.
/// </summary>
/// <remarks>
/// Inside this type the properties <c>InterfaceVersion</c>, <c>FirmwareVersion</c>, <c>ProductId</c>,
/// <c>SerialNumber</c> and <c>Options</c> hide the control item structs of the same names, so the structs are
/// written in full as <c>NetSdr.Items.*</c>.
/// </remarks>
public sealed record DeviceIdentity
{
    private readonly FrozenDictionary<Type, object> _facts;

    internal DeviceIdentity(DeviceIdentityBuilder source)
    {
        Name = source.Name;
        SerialNumber = source.SerialNumber;
        InterfaceVersion = source.InterfaceVersion;
        BootVersion = source.BootVersion;
        FirmwareVersion = source.FirmwareVersion;
        HardwareVersion = source.HardwareVersion;
        Fpga = source.Fpga;
        ProductId = source.ProductId;
        Options = source.Options;
        Unsupported = source.UnsupportedCodes.ToFrozenSet();
        _facts = source.Facts.ToFrozenDictionary();
    }

    /// <summary>Target name, item 0x0001.</summary>
    public string? Name { get; }

    /// <summary>Serial number, item 0x0002.</summary>
    public string? SerialNumber { get; }

    /// <summary>Interface (protocol) version, item 0x0003; 529 is 5.29.</summary>
    public Version? InterfaceVersion { get; }

    /// <summary>Boot code version, item 0x0004 with ID 0.</summary>
    public Version? BootVersion { get; }

    /// <summary>Firmware version, item 0x0004 with ID 1.</summary>
    public Version? FirmwareVersion { get; }

    /// <summary>Hardware version, item 0x0004 with ID 2.</summary>
    public Version? HardwareVersion { get; }

    /// <summary>FPGA configuration and revision, item 0x0004 with ID 3.</summary>
    public FpgaInfo? Fpga { get; }

    /// <summary>Product ID, item 0x0009.</summary>
    public uint? ProductId { get; }

    /// <summary>Hardware options, item 0x000A.</summary>
    public NetSdr.Items.Options? Options { get; }

    /// <summary>The device family, taken from <see cref="Name"/>.</summary>
    public KnownModel Model => KnownModelNames.FromName(Name);

    /// <summary>The item codes the device rejected with a NAK during identification.</summary>
    public IReadOnlySet<ushort> Unsupported { get; }

    /// <summary>The types of the facts probes have stored, for diagnostics.</summary>
    public IReadOnlyCollection<Type> FactTypes => _facts.Keys;

    /// <summary>Gets the fact of type <typeparamref name="TFact"/> that a probe stored.</summary>
    /// <returns><see langword="true"/> when there is such a fact.</returns>
    public bool TryGet<TFact>([MaybeNullWhen(false)] out TFact fact) where TFact : notnull
    {
        if (_facts.TryGetValue(typeof(TFact), out object? value))
        {
            fact = (TFact)value;
            return true;
        }

        fact = default;
        return false;
    }

    /// <summary>Gets the fact of type <typeparamref name="TFact"/> that a probe stored.</summary>
    /// <exception cref="KeyNotFoundException">No probe stored a fact of that type.</exception>
    public TFact Get<TFact>() where TFact : notnull =>
        TryGet<TFact>(out var fact)
            ? fact
            : throw new KeyNotFoundException($"Fact {typeof(TFact).Name} is not present in the device identity.");

    /// <summary>
    /// Reads the identity of the device <paramref name="client"/> is connected to: first the standard items (unless
    /// switched off in <paramref name="options"/>), then the probes of the application, in order. The client stays
    /// open and nothing is cached.
    /// </summary>
    /// <param name="client">A connected client.</param>
    /// <param name="options">What to read; <see langword="null"/> reads the standard items only.</param>
    /// <param name="ct">Cancels the reading.</param>
    /// <exception cref="TimeoutException">The device did not answer a request.</exception>
    /// <remarks>
    /// A NAK is not an error: the field stays <see langword="null"/> and the item code goes into
    /// <see cref="Unsupported"/>. Every other failure, such as a timeout, a lost connection or an unreadable reply,
    /// is thrown to the caller.
    /// </remarks>
    public static async Task<DeviceIdentity> ReadAsync(
        INetSdrControlClient client, IdentificationOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        options ??= new IdentificationOptions();
        var builder = new DeviceIdentityBuilder();

        if (options.IncludeStandardProbes)
        {
            await StandardProbes.RunAsync(client, builder, ct).ConfigureAwait(false);
        }

        foreach (ProbeAsync probe in options.Probes)
        {
            await probe(client, builder, ct).ConfigureAwait(false);
        }

        return builder.Current;
    }
}
