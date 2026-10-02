using System.Net;
using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Identification;

namespace NetSdr.Examples.Vega;

/// <summary>
/// A typed client of a Vega receiver, over a <see cref="NetSdrControlClient"/>. What is the same in every firmware
/// is here; what the firmware versions do differently is in <see cref="VegaV1Receiver"/> and <see cref="VegaV2Receiver"/>.
/// </summary>
public abstract class VegaReceiverBase : IAsyncDisposable
{
    /// <summary>Wraps a connected client whose device was identified as a Vega receiver. The receiver owns the client from now on.</summary>
    /// <param name="control">The connected client; <see cref="DisposeAsync"/> closes it.</param>
    /// <param name="identity">What identification learned about the device.</param>
    protected VegaReceiverBase(NetSdrControlClient control, DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(identity);

        Control = control;
        Identity = identity;
    }

    /// <summary>The underlying client, for the standard NetSDR items.</summary>
    public NetSdrControlClient Control { get; }

    /// <summary>What was learned about the device when it was connected.</summary>
    public DeviceIdentity Identity { get; }

    /// <summary>
    /// Connects to a Vega receiver, unlocks it and creates the client for its firmware version. The client is closed
    /// when anything fails.
    /// </summary>
    /// <param name="host">Host name or IP address of the receiver.</param>
    /// <param name="port">TCP port of the control channel.</param>
    /// <param name="unlockKey">The vendor key that unlocks the Vega items.</param>
    /// <param name="options">Settings of the control client.</param>
    /// <param name="ct">Cancels the connection and the identification.</param>
    /// <exception cref="VegaException">The device is not a Vega receiver.</exception>
    /// <exception cref="NetSdrNakException">The device rejected <paramref name="unlockKey"/>.</exception>
    public static Task<VegaReceiverBase> ConnectAsync(
        string host,
        int port,
        uint unlockKey,
        NetSdrControlClientOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return ConnectCoreAsync(unlockKey, options, catalog => catalog.ConnectAsync(host, port, ct));
    }

    /// <inheritdoc cref="ConnectAsync(string, int, uint, NetSdrControlClientOptions?, CancellationToken)"/>
    public static Task<VegaReceiverBase> ConnectAsync(
        IPEndPoint endPoint,
        uint unlockKey,
        NetSdrControlClientOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return ConnectCoreAsync(unlockKey, options, catalog => catalog.ConnectAsync(endPoint, ct));
    }

    /// <summary>Selects the antenna input of a channel.</summary>
    public Task SelectAntennaAsync(byte channel, AntennaPort port, CancellationToken ct = default) =>
        Control.SetAsync(new AntennaSelect(channel, port), ct);

    /// <summary>Gets the antenna input of a channel.</summary>
    public async Task<AntennaPort> GetAntennaAsync(byte channel, CancellationToken ct = default)
    {
        var reply = await Control.GetAsync<AntennaSelect, byte>(channel, ct).ConfigureAwait(false);
        return reply.Port;
    }

    /// <summary>Gets a temperature in degrees Celsius; the item layout depends on the firmware.</summary>
    /// <exception cref="NetSdrNakException">The device has no reading for <paramref name="sensor"/>.</exception>
    public abstract Task<double> GetTemperatureAsync(TemperatureSensor sensor, CancellationToken ct = default);

    /// <summary>Sets the device label.</summary>
    /// <exception cref="ArgumentException">
    /// The label is longer than <see cref="VegaProtocol.MaxLabelLength"/> characters or is not ASCII. Nothing is sent then.
    /// </exception>
    public Task SetLabelAsync(string label, CancellationToken ct = default) =>
        Control.SetAsync(new DeviceLabel(label), ct);

    /// <summary>Gets the device label.</summary>
    public async Task<string> GetLabelAsync(CancellationToken ct = default)
    {
        var reply = await Control.GetAsync<DeviceLabel>(ct).ConfigureAwait(false);
        return reply.Value;
    }

    /// <summary>Closes the control client.</summary>
    public ValueTask DisposeAsync() => Control.DisposeAsync();

    // The catalog creates the client and closes it on any failure, so nothing leaks from here.
    private static async Task<VegaReceiverBase> ConnectCoreAsync(
        uint unlockKey,
        NetSdrControlClientOptions? options,
        Func<DeviceCatalog<VegaReceiverBase>, Task<VegaReceiverBase>> connect)
    {
        var catalog = new DeviceCatalog<VegaReceiverBase>(
                new IdentificationOptions { Probes = { VegaProbes.Identify(unlockKey) } }, options)
            .Register("Vega v2",
                id => id.ProductId == VegaProtocol.ProductId && id.Get<VegaInfo>().Firmware >= new Version(2, 0),
                (client, id) => new VegaV2Receiver(client, id))
            .Register("Vega v1",
                id => id.ProductId == VegaProtocol.ProductId,
                (client, id) => new VegaV1Receiver(client, id));

        try
        {
            return await connect(catalog).ConfigureAwait(false);
        }
        catch (DeviceNotRecognizedException ex)
        {
            throw new VegaException(
                $"Device is not a Vega receiver (product id 0x{ex.Identity.ProductId?.ToString("X8") ?? "none"}).",
                ex.Identity);
        }
    }
}
