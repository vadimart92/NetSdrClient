using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Control;

/// <summary>Options, servers and reply helpers shared by the ResilientControlClient tests.</summary>
internal static class Resilient
{
    public const string GetStatus = "04 20 05 00";               // the verification and the heartbeat
    public const string Nak = "02 00";
    public const string ProductReply = "08 00 09 00 53 44 52 03";
    public const string VersionReply = "06 00 03 00 11 02";

    /// <summary>Short real timeouts, as ControlClientLifecycleTests.Fast(): a request's deadline is 750 ms after its write.</summary>
    public static ResilientControlClientOptions Fast(FakeLoggerFactory? logs = null) => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(150),
        LateReplyTimeout = TimeSpan.FromMilliseconds(600),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        LoggerFactory = logs is null ? NullLoggerFactory.Instance : logs,
        UseJitter = false,
    };

    /// <summary>For PipeConnector tests: no heartbeat frames on the pipe, optionally fake time.</summary>
    public static ResilientControlClientOptions Seam(FakeLoggerFactory? logs = null, TimeProvider? time = null)
    {
        var options = Fast(logs);
        options.HeartbeatInterval = Timeout.InfiniteTimeSpan;
        options.TimeProvider = time ?? TimeProvider.System;
        return options;
    }

    /// <summary>Sets <see cref="ResilientControlClientOptions.Rebooter"/> and <see cref="ResilientControlClientOptions.RecoveryPolicy"/>; a null policy means the default ladder.</summary>
    public static ResilientControlClientOptions WithRebooter(
        this ResilientControlClientOptions options, IDeviceRebooter rebooter, IRecoveryPolicy? policy = null)
    {
        options.Rebooter = rebooter;
        options.RecoveryPolicy = policy;
        return options;
    }

    /// <summary>Starts a test server and connects to it; on failure the server is disposed.</summary>
    public static async Task<(NetSdrTestServer Server, ResilientControlClient Client)> StartAsync(
        ResilientControlClientOptions options, Action<NetSdrTestServer>? setup = null)
    {
        var server = new NetSdrTestServer();
        try
        {
            setup?.Invoke(server);
            await server.StartAsync();
            using var timeout = new CancellationTokenSource(Limits.Test);
            return (server, await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), options, timeout.Token));
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public static IEnumerable<ControlRequest> WithoutStatus(this IEnumerable<ControlRequest> requests) =>
        requests.Where(r => r.Code != StatusCodes.Code);

    /// <summary>A handler that answers the first <paramref name="times"/> requests with <paramref name="first"/>, later ones with <paramref name="then"/> or Echo.</summary>
    public static Func<ControlRequest, ControlReply> FirstTimes(int times, ControlReply first, ControlReply? then = null)
    {
        int seen = 0;
        return _ => Interlocked.Increment(ref seen) <= times ? first : then ?? ControlReply.Echo;
    }

    public static Func<ControlRequest, ControlReply> Once(ControlReply first, ControlReply? then = null) => FirstTimes(1, first, then);

    /// <summary>A handler that drops the connection on the first request and answers later ones with <paramref name="then"/> or Echo.</summary>
    public static Func<ControlRequest, ControlReply> DropOnce(NetSdrTestServer server, ControlReply? then = null)
    {
        int seen = 0;
        return request =>
        {
            if (Interlocked.Increment(ref seen) > 1)
                return then ?? ControlReply.Echo;
            _ = server.DisconnectClientAsync();
            return ControlReply.Silent;
        };
    }

    public static Task Refused() => Task.FromException(new SocketException((int)SocketError.ConnectionRefused));

    /// <summary>Answers every request on the pipe with a NAK until the pipe ends or stays silent for Limits.Test.</summary>
    public static async Task NakEverythingAsync(this PipeDevice device)
    {
        try
        {
            while (true)
            {
                await device.ReadRequestAsync();
                await device.SendAsync(Nak);
            }
        }
        catch (Exception)
        {
            // The pipe is gone; the device has nothing left to answer.
        }
    }
}

/// <summary>The connect seam: each new inner client is attached to a fresh PipeDevice that the test drives.</summary>
internal sealed class PipeConnector(TimeProvider? time = null)
{
    private readonly Channel<PipeDevice> _devices = Channel.CreateUnbounded<PipeDevice>();
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>The (fake) time at the start of every attempt, the first ConnectAsync included.</summary>
    public ConcurrentQueue<DateTimeOffset> AttemptTimes { get; } = new();

    /// <summary>Runs first in attempt n (1 is ConnectAsync itself): a faulted task fails the attempt, a pending one delays it.</summary>
    public Func<int, CancellationToken, Task>? Before { get; init; }

    /// <summary>Started in the background for every attached device, for example NakEverythingAsync.</summary>
    public Func<PipeDevice, Task>? Serve { get; init; }

    public async Task ConnectAsync(NetSdrControlClient inner, CancellationToken ct)
    {
        int attempt = Interlocked.Increment(ref _attempts);
        AttemptTimes.Enqueue((time ?? TimeProvider.System).GetUtcNow());
        if (Before is { } before)
            await before(attempt, ct);
        var device = PipeDevice.Attach(inner);
        if (Serve is { } serve)
            _ = Task.Run(() => serve(device));
        _devices.Writer.TryWrite(device);
    }

    public async Task<PipeDevice> NextAsync() => await _devices.Reader.ReadAsync().AsTask().WaitAsync(Limits.Test);

    /// <summary>Connects through the seam; without <see cref="Serve"/> the first verification is answered here with a NAK.</summary>
    public async Task<(ResilientControlClient Client, PipeDevice Device)> StartAsync(ResilientControlClientOptions options)
    {
        var connecting = ResilientControlClient.ConnectAsync(ConnectAsync, "pipe", options, CancellationToken.None);
        var device = await NextAsync();
        if (Serve is null)
        {
            Assert.Equal(Hex.Parse(Resilient.GetStatus), await device.ReadRequestAsync());
            await device.SendAsync(Resilient.Nak);
        }

        return (await connecting.WaitAsync(Limits.Test), device);
    }
}
