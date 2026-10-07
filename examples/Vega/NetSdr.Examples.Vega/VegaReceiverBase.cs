using System.Net;
using System.Runtime.CompilerServices;
using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Framing;
using NetSdr.Identification;
using NetSdr.Items;

namespace NetSdr.Examples.Vega;

/// <summary>
/// A typed client of a Vega receiver, over an <see cref="INetSdrControlClient"/>. What is the same in every firmware
/// is here; what the firmware versions do differently is in <see cref="VegaV1Receiver"/> and <see cref="VegaV2Receiver"/>.
/// </summary>
public abstract class VegaReceiverBase : IAsyncDisposable
{
    /// <summary>Wraps a connected client whose device was identified as a Vega receiver. The receiver owns the client from now on.</summary>
    /// <param name="control">The connected client; <see cref="DisposeAsync"/> closes it.</param>
    /// <param name="identity">What identification learned about the device.</param>
    protected VegaReceiverBase(INetSdrControlClient control, DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(identity);

        Control = control;
        Identity = identity;
    }

    /// <summary>The underlying client, for the standard NetSDR items.</summary>
    public INetSdrControlClient Control { get; }

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

    /// <summary>
    /// Points the data output at <paramref name="target"/> and starts a 16-bit I/Q stream of channel 0. The requests go
    /// in this order: sample rate, frequency, destination, then the receiver state that starts the capture.
    /// </summary>
    /// <param name="target">Where the receiver sends the UDP data; an IPv4 end point, usually that of a <see cref="NetSdr.Data.NetSdrDataReceiver"/>.</param>
    /// <param name="frequencyHz">Receiver frequency in hertz, at most <see cref="UInt40.MaxValue"/>.</param>
    /// <param name="sampleRate">Output sample rate in hertz.</param>
    /// <param name="ct">Cancels the wait for the replies.</param>
    /// <exception cref="ArgumentException"><paramref name="target"/> is not an IPv4 end point. Nothing is sent then.</exception>
    /// <exception cref="OverflowException"><paramref name="frequencyHz"/> does not fit in 40 bits. Nothing is sent then.</exception>
    public async Task StartStreamAsync(IPEndPoint target, ulong frequencyHz, uint sampleRate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Everything that can be rejected is built before the first request, so a bad argument leaves the device untouched.
        var rate = new OutputSampleRate(0, sampleRate);
        var frequency = new ReceiverFrequency(ReceiverFrequency.Channel1, frequencyHz);
        var destination = DataOutputUdpAddress.For(target);

        await Control.SetAsync(rate, ct).ConfigureAwait(false);
        await Control.SetAsync(frequency, ct).ConfigureAwait(false);
        await Control.SetAsync(destination, ct).ConfigureAwait(false);
        await Control.SetAsync(ReceiverState.Start(complex: true, bits24: false), ct).ConfigureAwait(false);
    }

    /// <summary>Stops the stream.</summary>
    public Task StopStreamAsync(CancellationToken ct = default) =>
        Control.SetAsync(ReceiverState.Stop, ct);

    /// <summary>
    /// Reads the events the receiver sends on its own, in the order they arrive. The sequence ends when the control
    /// client is closed or fails. Messages that are not events of this class, or whose payload cannot be read, are skipped.
    /// </summary>
    /// <remarks>
    /// The events come from <see cref="INetSdrControlClient.Unsolicited"/>, which has one reader, so only one enumeration
    /// at a time is supported.
    /// </remarks>
    /// <param name="ct">Stops the enumeration; it then throws <see cref="OperationCanceledException"/>.</param>
    public async IAsyncEnumerable<VegaEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var message in Control.Unsolicited.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (message.Type != ReplyType.Unsolicited)
            {
                continue;
            }

            if (ParseEvent(message) is { } vegaEvent)
            {
                yield return vegaEvent;
            }
        }
    }

    /// <summary>Closes the control client.</summary>
    public ValueTask DisposeAsync() => Control.DisposeAsync();

    /// <summary>
    /// Turns a message into an event of this firmware version. <see cref="OverloadEvent"/> is the same in every version
    /// and is handled by the base class; the rest is left to <see cref="TryParseEvent"/>.
    /// </summary>
    /// <param name="message">An unsolicited message, which <see cref="ReadEventsAsync"/> has already selected.</param>
    /// <returns>The event, or <see langword="null"/> for a message that is not one.</returns>
    /// <exception cref="NetSdrProtocolException">
    /// The payload of the item cannot be read, which <see cref="ControlItemMessage.As{T}"/> reports; <see cref="ReadEventsAsync"/>
    /// skips such a message.
    /// </exception>
    protected abstract VegaEvent? TryParseEvent(in ControlItemMessage message);

    // One telemetry frame with a broken payload must not end the stream of events, so a read failure is a skipped message.
    private VegaEvent? ParseEvent(in ControlItemMessage message)
    {
        try
        {
            if (message.Is<OverloadEvent>())
            {
                var overload = message.As<OverloadEvent>();
                return new OverloadDetected(overload.Channel, overload.Flags);
            }

            return TryParseEvent(in message);
        }
        catch (NetSdrProtocolException)
        {
            return null;
        }
    }

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
