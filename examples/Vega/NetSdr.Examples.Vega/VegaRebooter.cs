using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetSdr.Control;

namespace NetSdr.Examples.Vega;

/// <summary>Settings of a <see cref="VegaRebooter"/>.</summary>
public sealed class VegaRebooterOptions
{
    /// <summary>The host of the service; <see langword="null"/> takes the address from the <see cref="RebootContext"/>.</summary>
    public string? Host { get; init; }

    /// <summary>The TCP port of the service, <see cref="VegaProtocol.ServicePort"/> by default.</summary>
    public int Port { get; init; } = VegaProtocol.ServicePort;

    /// <summary>The unlock key of the receiver, the same as for <see cref="Items.VendorUnlock"/>.</summary>
    public uint UnlockKey { get; init; }

    /// <summary>How long the receiver needs after an accepted soft reboot, 8 s by default.</summary>
    public TimeSpan SoftBootTime { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the receiver needs after an accepted hard reboot, 20 s by default.</summary>
    public TimeSpan HardBootTime { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Reboots a Vega receiver through the service protocol of its independent microcontroller (see <see cref="VegaProtocol.ServicePort"/>):
/// one TCP connection per request, 8 bytes out, 4 bytes back.
/// </summary>
public sealed class VegaRebooter : IDeviceRebooter
{
    private const int RequestSize = 8;
    private const int ReplySize = 4;

    private readonly VegaRebooterOptions _options;

    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The port is outside 1..65535 or a boot time is negative.</exception>
    public VegaRebooter(VegaRebooterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Port, IPEndPoint.MinPort + 1, nameof(options.Port));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, IPEndPoint.MaxPort, nameof(options.Port));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SoftBootTime, TimeSpan.Zero, nameof(options.SoftBootTime));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.HardBootTime, TimeSpan.Zero, nameof(options.HardBootTime));
        _options = options;
    }

    /// <summary>Sends the reboot request to the service and completes when the service has accepted it.</summary>
    /// <exception cref="IOException">The service closed the connection before answering, or answered with an unexpected reply.</exception>
    /// <exception cref="VegaException">The service refused the reboot: a wrong unlock key, busy, or another status.</exception>
    public async Task RebootAsync(RebootKind kind, RebootContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        string host = _options.Host ?? context.LastRemoteEndPoint?.Address.ToString() ?? HostOf(context.Target);
        byte command = kind == RebootKind.Hard ? VegaProtocol.HardRebootCommand : VegaProtocol.SoftRebootCommand;

        var request = new byte[RequestSize];
        request[0] = VegaProtocol.ServiceMagic0;
        request[1] = VegaProtocol.ServiceMagic1;
        request[2] = VegaProtocol.ServiceVersion;
        request[3] = command;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), _options.UnlockKey);

        var reply = new byte[ReplySize];
        using (var socket = new Socket(SocketType.Stream, ProtocolType.Tcp))
        {
            await socket.ConnectAsync(host, _options.Port, ct).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await stream.WriteAsync(request, ct).ConfigureAwait(false);
            try
            {
                await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new IOException("The Vega service closed the connection before answering.", ex);
            }
        }

        if (reply[0] != VegaProtocol.ServiceMagic0 || reply[1] != VegaProtocol.ServiceMagic1 || reply[2] != command)
        {
            throw new IOException("The Vega service answered with an unexpected reply.");
        }

        byte status = reply[3];
        switch (status)
        {
            case VegaProtocol.ServiceAccepted:
                return;
            case VegaProtocol.ServiceBadKey:
                throw new VegaException("The Vega service rejected the reboot: wrong unlock key.");
            case VegaProtocol.ServiceBusy:
                throw new VegaException("The Vega service is busy and did not accept the reboot.");
            default:
                throw new VegaException($"The Vega service answered with status {status}.");
        }
    }

    /// <summary><see cref="VegaRebooterOptions.SoftBootTime"/> or <see cref="VegaRebooterOptions.HardBootTime"/>.</summary>
    public TimeSpan GetBootTime(RebootKind kind) => kind == RebootKind.Hard ? _options.HardBootTime : _options.SoftBootTime;

    /// <summary>The host of a "host:port" target or an end point; a target without a colon is the host itself.</summary>
    private static string HostOf(string target)
    {
        if (IPEndPoint.TryParse(target, out IPEndPoint? endPoint))
        {
            return endPoint.Address.ToString();
        }

        int colon = target.LastIndexOf(':');
        return colon < 0 ? target : target[..colon];
    }
}
