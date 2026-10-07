using System.Buffers.Binary;
using NetSdr.Control;
using NetSdr.Examples.Vega.Items;
using NetSdr.Framing;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Examples.Vega.Tests;

/// <summary>The firmware generation a <see cref="VegaEmulator"/> imitates.</summary>
public enum VegaFirmware
{
    /// <summary>Temperature as <see cref="BoardTemperatureV1"/>, no item 0x8005.</summary>
    V1,

    /// <summary>Temperature as <see cref="BoardTemperatureV2"/>, item 0x8005 reports version 2.00.</summary>
    V2,
}

/// <summary>How a <see cref="VegaEmulator"/> hangs.</summary>
public enum VegaHang
{
    /// <summary>The emulator answers.</summary>
    None,

    /// <summary>The firmware hangs: no replies and no UDP stream until any accepted reboot.</summary>
    Firmware,

    /// <summary>The board hangs: no replies, a soft reboot does not cure it, only a hard one.</summary>
    Board,
}

/// <summary>
/// A Vega receiver imitated on a <see cref="NetSdrTestServer"/>: the standard items come from the server's state, the
/// Vega items from handlers of this class. The handlers run on the connection thread of the server, so the state they
/// share with the test is guarded by a lock. A <see cref="VegaServiceServer"/> on its own loopback port imitates the
/// service microcontroller: it accepts soft and hard reboots, which drop the client, refuse connections for the boot
/// time and lock the receiver again.
/// </summary>
public sealed class VegaEmulator : IAsyncDisposable
{
    public const uint DefaultKey = 0xC0DE_5EC5;

    // Item 0x8005 of firmware v2 holds the version in hundredths: 2.00.
    private const ushort FirmwareV2Version = 200;

    private readonly uint _unlockKey;
    private readonly Lock _sync = new();
    private readonly Dictionary<byte, AntennaPort> _antennas = [];
    private readonly Dictionary<TemperatureSensor, double> _temperatures = [];
    private bool _unlocked;
    private string _label = string.Empty;
    private VegaHang _hang;
    private bool _booting;
    private readonly List<(RebootKind Kind, bool KeyValid)> _rebootRequests = [];

    public VegaEmulator(uint unlockKey = DefaultKey, VegaFirmware firmware = VegaFirmware.V2)
    {
        _unlockKey = unlockKey;
        Firmware = firmware;
        Service = new VegaServiceServer(Respond);

        Server.Preload(new ProductId(VegaProtocol.ProductId));

        Server.OnRequest<VendorUnlock>(Unlock);

        OnUnlocked<AntennaSelect>(request => request.Type switch
        {
            RequestType.Set => SetAntenna(request.Item),
            RequestType.Get => GetAntenna(request.Key<byte>()),
            _ => ControlReply.Nak,
        });

        // The code is the same in both firmware versions; only the encoding of the reply differs.
        Server.OnRequest(VegaProtocol.BoardTemperatureCode, request =>
            IsUnlocked && request.Type == RequestType.Get && request.Payload.Length >= 1
                ? GetTemperature((TemperatureSensor)request.Payload.Span[0])
                : ControlReply.Nak);

        OnUnlocked<DeviceLabel>(request => request.Type switch
        {
            RequestType.Set when request.Item.Value.Length <= VegaProtocol.MaxLabelLength => SetLabel(request.Item),
            RequestType.Get => GetLabel(),
            _ => ControlReply.Nak,
        });

        // The device never answers a request for an event.
        Server.OnRequest(VegaProtocol.OverloadEventCode, _ => ControlReply.Nak);

        OnUnlocked<VegaFirmwareInfo>(request =>
            request.Type == RequestType.Get && Firmware == VegaFirmware.V2
                ? ControlReply.Item(new VegaFirmwareInfo(FirmwareV2Version))
                : ControlReply.Nak);
    }

    /// <summary>The server behind the emulator, for the requests it has received.</summary>
    public NetSdrTestServer Server { get; } = new();

    public VegaFirmware Firmware { get; }

    public int Port => Server.Port;

    /// <summary>The service microcontroller of the emulator, for the requests it has received.</summary>
    public VegaServiceServer Service { get; }

    /// <summary>The TCP port of the service protocol.</summary>
    public int ServicePort => Service.Port;

    /// <summary>How long the receiver refuses connections after an accepted soft reboot.</summary>
    public TimeSpan SoftBootTime { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the receiver refuses connections after an accepted hard reboot.</summary>
    public TimeSpan HardBootTime { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Every well-formed reboot request the service has received, accepted or not, oldest first.</summary>
    public IReadOnlyList<(RebootKind Kind, bool KeyValid)> RebootRequests
    {
        get
        {
            lock (_sync)
            {
                return [.. _rebootRequests];
            }
        }
    }

    /// <summary>Whether a <see cref="VendorUnlock"/> with the right key has been received; it lasts until a reboot.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_sync)
            {
                return _unlocked;
            }
        }
    }

    public Task StartAsync() => Task.WhenAll(Server.StartAsync(), Service.StartAsync());

    /// <summary>
    /// Makes the receiver hang, or stop hanging with <see cref="VegaHang.None"/>. A hang leaves the requests
    /// unanswered and stops the UDP stream; while the receiver boots its availability is left to the end of the boot.
    /// </summary>
    public void Hang(VegaHang kind)
    {
        bool booting;
        lock (_sync)
        {
            _hang = kind;
            booting = _booting;
        }

        if (!booting)
        {
            Server.Availability = kind == VegaHang.None ? ServerAvailability.Normal : ServerAvailability.Silent;
        }

        if (kind != VegaHang.None)
        {
            _ = Server.StopStreamingAsync();
        }
    }

    /// <summary>Sets the temperature a Get of the sensor answers with.</summary>
    public void SetTemperature(TemperatureSensor sensor, double celsius)
    {
        lock (_sync)
        {
            _temperatures[sensor] = celsius;
        }
    }

    /// <summary>Sends a temperature as an unsolicited item, in the format of the firmware.</summary>
    public Task SendTemperatureAsync(TemperatureSensor sensor, double celsius) =>
        Firmware == VegaFirmware.V1
            ? Server.SendUnsolicitedAsync(BoardTemperatureV1.FromCelsius(sensor, celsius))
            : Server.SendUnsolicitedAsync(BoardTemperatureV2.FromCelsius(sensor, celsius, status: 0));

    public Task SendOverloadAsync(byte channel, OverloadFlags flags) =>
        Server.SendUnsolicitedAsync(new OverloadEvent(channel, flags));

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Service.DisposeAsync();
        }
        finally
        {
            await Server.DisposeAsync();
        }
    }

    /// <summary>Answers one service request; a malformed one gets no reply and is not recorded.</summary>
    private ReadOnlyMemory<byte> Respond(ReadOnlyMemory<byte> request)
    {
        ReadOnlySpan<byte> bytes = request.Span;
        if (bytes.Length < 8
            || bytes[0] != VegaProtocol.ServiceMagic0 || bytes[1] != VegaProtocol.ServiceMagic1 || bytes[2] != VegaProtocol.ServiceVersion
            || bytes[3] is not (VegaProtocol.SoftRebootCommand or VegaProtocol.HardRebootCommand))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        byte command = bytes[3];
        RebootKind kind = command == VegaProtocol.HardRebootCommand ? RebootKind.Hard : RebootKind.Soft;
        bool keyValid = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == _unlockKey;
        byte status;
        lock (_sync)
        {
            _rebootRequests.Add((kind, keyValid));
            if (_booting)
            {
                status = VegaProtocol.ServiceBusy;
            }
            else if (!keyValid)
            {
                status = VegaProtocol.ServiceBadKey;
            }
            else
            {
                _booting = true;
                status = VegaProtocol.ServiceAccepted;
            }
        }

        if (status == VegaProtocol.ServiceAccepted)
        {
            _ = Task.Run(() => BootAsync(kind));
        }

        return new byte[] { VegaProtocol.ServiceMagic0, VegaProtocol.ServiceMagic1, command, status };
    }

    /// <summary>
    /// One reboot: the client is dropped, connections are refused for the boot time and the receiver is locked again;
    /// a soft reboot cures a firmware hang, a hard one any hang and also resets the state of the receiver.
    /// </summary>
    private async Task BootAsync(RebootKind kind)
    {
        try
        {
            await Server.DisconnectClientAsync();
            Server.Availability = ServerAvailability.CloseOnAccept;
            lock (_sync)
            {
                _unlocked = false;
                if (kind == RebootKind.Hard || _hang == VegaHang.Firmware)
                {
                    _hang = VegaHang.None;
                }

                if (kind == RebootKind.Hard)
                {
                    _antennas.Clear();
                    _label = string.Empty;
                }
            }

            if (kind == RebootKind.Hard)
            {
                Server.ClearState();
                Server.Preload(new ProductId(VegaProtocol.ProductId));
            }

            await Task.Delay(kind == RebootKind.Hard ? HardBootTime : SoftBootTime);
        }
        finally
        {
            lock (_sync)
            {
                Server.Availability = _hang == VegaHang.Board ? ServerAvailability.Silent : ServerAvailability.Normal;
                _booting = false;
            }
        }
    }

    /// <summary>Handles an item that is only served once the receiver is unlocked; before that it is a NAK.</summary>
    private void OnUnlocked<T>(Func<ControlRequest<T>, ControlReply> handler) where T : struct, IControlItem<T> =>
        Server.OnRequest<T>(request => IsUnlocked ? handler(request) : ControlReply.Nak);

    private ControlReply Unlock(ControlRequest<VendorUnlock> request)
    {
        if (request.Type != RequestType.Set || request.Item.Key != _unlockKey)
        {
            return ControlReply.Nak;
        }

        lock (_sync)
        {
            _unlocked = true;
        }

        return ControlReply.Echo;
    }

    private ControlReply SetAntenna(AntennaSelect item)
    {
        lock (_sync)
        {
            _antennas[item.Channel] = item.Port;
        }

        return ControlReply.Echo;
    }

    private ControlReply GetAntenna(byte channel)
    {
        AntennaPort port;
        lock (_sync)
        {
            port = _antennas.GetValueOrDefault(channel, AntennaPort.A);
        }

        return ControlReply.Item(new AntennaSelect(channel, port));
    }

    private ControlReply GetTemperature(TemperatureSensor sensor)
    {
        double celsius;
        lock (_sync)
        {
            if (!_temperatures.TryGetValue(sensor, out celsius))
            {
                return ControlReply.Nak;
            }
        }

        return Firmware == VegaFirmware.V1
            ? ControlReply.Item(BoardTemperatureV1.FromCelsius(sensor, celsius))
            : ControlReply.Item(BoardTemperatureV2.FromCelsius(sensor, celsius, status: 0));
    }

    private ControlReply SetLabel(DeviceLabel item)
    {
        lock (_sync)
        {
            _label = item.Value;
        }

        return ControlReply.Echo;
    }

    private ControlReply GetLabel()
    {
        string label;
        lock (_sync)
        {
            label = _label;
        }

        return ControlReply.Item(new DeviceLabel(label));
    }
}
