using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetSdr.Framing;
using NetSdr.Items;
using Polly;
using Polly.Retry;

namespace NetSdr.Control;

/// <summary>
/// A control client that survives the loss of its connection: it reconnects with a backoff, restores the device's
/// session through <see cref="ResilientControlClientOptions.ConnectionRestored"/>, retries the commands the loss
/// interrupted, waits for the late reply of a request a busy device answered after its timeout instead of sending
/// it again, and keeps <see cref="Unsolicited"/> and <see cref="Completion"/> across connections.
/// One connection exists at a time; one command of the application runs at a time, in the order of the calls.
/// </summary>
public sealed partial class ResilientControlClient : INetSdrControlClient
{
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ControlItemMessage> _unsolicited;
    private readonly ResiliencePipeline _commandPipeline;
    private readonly ResiliencePipeline _reconnect;
    private readonly NetSdrControlClientOptions _innerOptions;
    private readonly ResilientControlClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<NetSdrControlClient, CancellationToken, Task> _connect;
    private readonly string _target;
    private readonly Action<Task<ControlItemMessage>, object?> _settle;
    private readonly IDeviceRebooter? _rebooter;
    private readonly IRecoveryPolicy? _policy;       // null exactly when _rebooter is: without a rebooter it is never called
    private readonly int _connectAttempts;

    /// <summary>The scope of the running <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback, seen by everything it runs (spec 7.5).</summary>
    private readonly AsyncLocal<RestoreScope?> _restoreScope = new();

    // Guarded by _sync. _state and _link are also read without the lock by IsConnected and the end points.
    private volatile ClientState _state = ClientState.Reconnecting;
    private volatile Link _link = null!;    // published by ConnectCoreAsync before the instance is handed out
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private long _lastAttemptStart;

    /// <summary>The link of the reconnection attempt in progress, from its pump start until it is published or closed; what <see cref="DisposeAsync"/> closes besides <see cref="_link"/>.</summary>
    private Link? _restoring;

    /// <summary>The failure the client gave up with; <see langword="null"/> while it has not, and after a plain disposal.</summary>
    private IOException? _failure;

    /// <summary>Why the last published connection was lost: the cause a command that times out while reconnecting reports.</summary>
    private Exception? _lastLoss;

    private ResilientControlClient(
        Func<NetSdrControlClient, CancellationToken, Task> connect, string target, ResilientControlClientOptions options)
    {
        _connect = connect;
        _target = target;
        _options = options;
        _time = options.TimeProvider;
        _logger = options.LoggerFactory.CreateLogger(typeof(ResilientControlClient).FullName!);
        _settle = (_, state) => Settle((Exchange)state!);
        _rebooter = options.Rebooter;
        _policy = _rebooter is null ? null : options.RecoveryPolicy ?? new EscalatingRecoveryPolicy();
        _connectAttempts = options.ConnectAttempts ?? (_rebooter is null ? 1 : 8);
        _innerOptions = new NetSdrControlClientOptions
        {
            ResponseTimeout = options.ResponseTimeout,          // finite: tells a busy device from a dead connection
            UnsolicitedCapacity = options.UnsolicitedCapacity,  // the pump drains the channel continuously
            FaultOnTimeout = false,
            LoggerFactory = options.LoggerFactory,
            TimeProvider = options.TimeProvider,                // ResponseTimeout on the same clock
            Supervised = true,                                  // levels of spec 3.2
        };
        _unsolicited = Channel.CreateBounded<ControlItemMessage>(new BoundedChannelOptions(options.UnsolicitedCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _commandPipeline = new ResiliencePipelineBuilder { TimeProvider = options.TimeProvider }
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,                      // 4 attempts; CommandTimeout bounds them further
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
                ShouldHandle = static a => ValueTask.FromResult(ShouldRetryCommand(a.Context, a.Outcome.Exception)),
                OnRetry = static a =>
                {
                    CommandExecution exec = a.Context.Properties.GetValue(CommandExecution.ExecKey, null!);
                    exec.LastError = a.Outcome.Exception;
                    exec.Owner.LogCommandRetrying(exec, a.AttemptNumber + 1, a.Outcome.Exception!);
                    return default;
                },
            })
            .Build();
        // Polly needs MaxRetryAttempts >= 1, so with one attempt per loss there is no retry strategy at all.
        _reconnect = options.ReconnectAttempts == 1
            ? ResiliencePipeline.Empty
            : new ResiliencePipelineBuilder { TimeProvider = options.TimeProvider }
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = options.ReconnectAttempts - 1,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(30),
                    UseJitter = options.UseJitter,
                    // Cancellation is the disposal or a reboot request, never a failed attempt; a reentrant
                    // ConnectionRestored callback is fatal (spec 7.5), so the client gives up instead of trying again;
                    // a decision of the recovery policy ends the series (reboot spec 5.3); and the attempt limit holds
                    // across every series of the loss (Ruling 3).
                    ShouldHandle = a => ValueTask.FromResult(
                        a.Outcome.Exception is not (null or FatalRestoreException or RebootScheduledException or RecoveryGaveUpException)
                        && !a.Context.CancellationToken.IsCancellationRequested
                        && a.Context.Properties.GetValue(ReconnectKey, null!).Attempt < options.ReconnectAttempts),
                    OnRetry = a =>
                    {
                        // OnRetryArguments carries no TState: the counter and the phase come from the context.
                        ReconnectState state = a.Context.Properties.GetValue(ReconnectKey, null!);
                        ResilientClientLog.ReconnectAttemptFailed(
                            _logger, state.Attempt, _target, state.Phase, a.RetryDelay, a.Outcome.Exception!);
                        return default;
                    },
                })
                .Build();
    }

    /// <summary>
    /// Everything the device sends other than a reply to a request, from every connection in turn: <c>Unsolicited</c>
    /// frames, data items, acknowledgements, late replies and responses nobody waits for. A lost connection does not
    /// end it; it completes without error after <see cref="DisposeAsync"/> or when the client gave up reconnecting,
    /// and only after <see cref="Completion"/> has its outcome.
    /// </summary>
    public ChannelReader<ControlItemMessage> Unsolicited => _unsolicited.Reader;

    /// <summary>
    /// Completes successfully after <see cref="DisposeAsync"/>, and with the <see cref="IOException"/> of the last
    /// attempt when the client gave up reconnecting. A lost connection does not complete it.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Whether the published connection is alive: <see langword="false"/> while reconnecting, while
    /// <see cref="ResilientControlClientOptions.ConnectionRestored"/> runs and after the client is closed.
    /// </summary>
    public bool IsConnected => _state == ClientState.Connected && _link.Client.IsConnected;

    /// <summary>
    /// The local end of the last published connection; while reconnecting that of the lost one. The port is new on
    /// every reconnection, so a data stream is addressed from <see cref="ConnectionRestoredContext.Client"/>.
    /// </summary>
    public IPEndPoint? LocalEndPoint => _link.Client.LocalEndPoint;

    /// <summary>The device's end of the last published connection; while reconnecting that of the lost one.</summary>
    public IPEndPoint? RemoteEndPoint => _link.Client.RemoteEndPoint;

    /// <summary>
    /// Connects to the device and verifies the connection with a <c>Get</c> of the status codes, which a NAK also
    /// passes. There is one attempt and <see cref="ResilientControlClientOptions.ConnectionRestored"/> is not called;
    /// on failure nothing is left running. The host name is resolved again on every reconnection.
    /// </summary>
    /// <param name="host">Host name or IP address of the device.</param>
    /// <param name="port">TCP port of the control channel; the device listens on 50000 by default.</param>
    /// <param name="options">The settings, validated and copied before the first await; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of its range.</exception>
    /// <exception cref="ArgumentNullException"><see cref="ResilientControlClientOptions.LoggerFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="SocketException">The TCP connection failed.</exception>
    /// <exception cref="TimeoutException">No TCP connection within <see cref="ResilientControlClientOptions.ConnectTimeout"/>, or no answer to the verification.</exception>
    /// <exception cref="IOException">The connection was lost before it was verified.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Task<ResilientControlClient> ConnectAsync(
        string host, int port = 50000, ResilientControlClientOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return ConnectAsync((inner, t) => inner.ConnectAsync(host, port, t), $"{host}:{port}", options, ct);
    }

    /// <summary>
    /// Connects to the device and verifies the connection with a <c>Get</c> of the status codes, which a NAK also
    /// passes. There is one attempt and <see cref="ResilientControlClientOptions.ConnectionRestored"/> is not called;
    /// on failure nothing is left running.
    /// </summary>
    /// <param name="endPoint">The control channel of the device.</param>
    /// <param name="options">The settings, validated and copied before the first await; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of its range.</exception>
    /// <exception cref="ArgumentNullException"><see cref="ResilientControlClientOptions.LoggerFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="SocketException">The TCP connection failed.</exception>
    /// <exception cref="TimeoutException">No TCP connection within <see cref="ResilientControlClientOptions.ConnectTimeout"/>, or no answer to the verification.</exception>
    /// <exception cref="IOException">The connection was lost before it was verified.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Task<ResilientControlClient> ConnectAsync(
        IPEndPoint endPoint, ResilientControlClientOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return ConnectAsync((inner, t) => inner.ConnectAsync(endPoint, t), endPoint.ToString(), options, ct);
    }

    /// <summary>
    /// The connect seam of the tests: <paramref name="connect"/> attaches every new inner client, for example to a
    /// <c>PipeDevice</c>, or throws right away.
    /// </summary>
    /// <param name="target">What the logs and the error messages call the device.</param>
    internal static Task<ResilientControlClient> ConnectAsync(
        Func<NetSdrControlClient, CancellationToken, Task> connect, string target,
        ResilientControlClientOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(target);
        var client = new ResilientControlClient(connect, target, Validated(options));
        return client.ConnectCoreAsync(ct);
    }

    /// <summary>Sets a control item and returns the item the device echoes back.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The item does not fit in one frame.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The client gave up reconnecting; the cause is the inner exception.</exception>
    /// <exception cref="TimeoutException">The command did not complete within <see cref="ResilientControlClientOptions.CommandTimeout"/>; the cause of the delay, when known, is the inner exception.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; a request already written stays on the line, and its late reply never answers another.</exception>
    public Task<T> SetAsync<T>(T item, CancellationToken ct = default) where T : struct, IControlItem<T>
    {
        ThrowIfReentrant();
        ThrowIfClosed();
        return DecodeAsync<T>(CommandAsync(RequestType.Set, T.Code, Encode(in item), typeof(T).Name, ct, null));
    }

    /// <summary>Requests a control item that needs no key.</summary>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The client gave up reconnecting; the cause is the inner exception.</exception>
    /// <exception cref="TimeoutException">The command did not complete within <see cref="ResilientControlClientOptions.CommandTimeout"/>; the cause of the delay, when known, is the inner exception.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; a request already written stays on the line, and its late reply never answers another.</exception>
    public Task<T> GetAsync<T>(CancellationToken ct = default) where T : struct, IControlItem<T>
    {
        ThrowIfReentrant();
        ThrowIfClosed();
        return RequestAsync<T>(RequestType.Get, ReadOnlySpan<byte>.Empty, null, ct, null);
    }

    /// <summary>Requests a control item identified by <paramref name="key"/>, sent as its raw little-endian bytes (for example a channel number).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key does not fit in one frame.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The client gave up reconnecting; the cause is the inner exception.</exception>
    /// <exception cref="TimeoutException">The command did not complete within <see cref="ResilientControlClientOptions.CommandTimeout"/>; the cause of the delay, when known, is the inner exception.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; a request already written stays on the line, and its late reply never answers another.</exception>
    public Task<T> GetAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged
    {
        ThrowIfReentrant();
        ThrowIfClosed();
        return RequestAsync<T>(RequestType.Get, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), nameof(key), ct, null);
    }

    /// <summary>Requests the range of a control item; the device answers with a <c>RangeResponse</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key does not fit in one frame.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The client gave up reconnecting; the cause is the inner exception.</exception>
    /// <exception cref="TimeoutException">The command did not complete within <see cref="ResilientControlClientOptions.CommandTimeout"/>; the cause of the delay, when known, is the inner exception.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; a request already written stays on the line, and its late reply never answers another.</exception>
    public Task<T> GetRangeAsync<T, TKey>(TKey key, CancellationToken ct = default)
        where T : struct, IControlItem<T> where TKey : unmanaged
    {
        ThrowIfReentrant();
        ThrowIfClosed();
        return RequestAsync<T>(RequestType.GetRange, MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key)), nameof(key), ct, null);
    }

    /// <summary>
    /// Sends a request for any item code and returns the device's reply uninterpreted. The reply type is
    /// <c>RangeResponse</c> for <see cref="RequestType.GetRange"/> and <c>Response</c> otherwise.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> is not <c>Set</c>, <c>Get</c> or <c>GetRange</c>, or the payload does not fit in one frame.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The client gave up reconnecting; the cause is the inner exception.</exception>
    /// <exception cref="TimeoutException">The command did not complete within <see cref="ResilientControlClientOptions.CommandTimeout"/>; the cause of the delay, when known, is the inner exception.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; a request already written stays on the line, and its late reply never answers another.</exception>
    public Task<ControlItemMessage> SendAsync(
        RequestType type, ushort code, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ThrowIfReentrant();
        ThrowIfClosed();
        return CommandAsync(type, code, CopyPayload(type, payload), null, ct, null);
    }

    /// <summary>
    /// Reboots the device through <see cref="ResilientControlClientOptions.Rebooter"/> and completes once the client is
    /// connected again after the reboot. On a live connection the client ends it itself (no 1103): the request in flight
    /// is retried after the restore, as after any loss. A request made while another has not started yet merges with it
    /// (Hard wins); one made while a reboot runs or its boot wait lasts joins that reboot, whatever its kind.
    /// </summary>
    /// <param name="kind">How to reboot the device.</param>
    /// <param name="ct">Cancels only this caller's wait: once accepted, the reboot itself still happens.</param>
    /// <exception cref="InvalidOperationException">
    /// Called from inside <see cref="ResilientControlClientOptions.ConnectionRestored"/> (the client gives up), no
    /// <see cref="ResilientControlClientOptions.Rebooter"/> is configured, or the client gave up reconnecting (the cause
    /// is the inner exception).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="TimeoutException">In the task: the transport did not complete within <see cref="ResilientControlClientOptions.RebootTimeout"/>.</exception>
    /// <exception cref="OperationCanceledException">In the task: <paramref name="ct"/> was cancelled.</exception>
    /// <remarks>
    /// The task also fails with whatever the transport threw, with <see cref="ObjectDisposedException"/> when the client
    /// is disposed meanwhile, and with the give-up failure when the client gives up. A failed reboot leaves the
    /// connection closed, and the client goes on reconnecting.
    /// </remarks>
    public Task RebootAsync(RebootKind kind, CancellationToken ct = default)
    {
        ThrowIfReentrant();
        ThrowIfClosed();
        if (_rebooter is null)
        {
            throw new InvalidOperationException("No Rebooter is configured; set ResilientControlClientOptions.Rebooter.");
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled(ct);
        }

        Task completion = RegisterRebootRequest(kind);
        try
        {
            ResilientClientLog.RebootRequested(_logger, kind, _target);
        }
        catch (Exception)
        {
            // A logging provider failed; the request is registered and runs.
        }

        // Cancelling ct cancels only this caller's task, never the reboot (reboot spec 4.2).
        return completion.WaitAsync(ct);
    }

    /// <summary>Rejects options outside their ranges and returns a copy the client keeps.</summary>
    private static ResilientControlClientOptions Validated(ResilientControlClientOptions? options)
    {
        options ??= new ResilientControlClientOptions();
        ArgumentNullException.ThrowIfNull(options.LoggerFactory, nameof(options));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options));
        RequireFinite(options.ResponseTimeout, nameof(options.ResponseTimeout), nameof(options));
        RequireFinite(options.LateReplyTimeout, nameof(options.LateReplyTimeout), nameof(options));
        TimeSpan unanswered = options.ResponseTimeout + options.LateReplyTimeout;
        if (unanswered.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), unanswered, "ResponseTimeout + LateReplyTimeout must be at most Int32.MaxValue milliseconds.");
        }

        RequireFiniteOrInfinite(options.CommandTimeout, nameof(options.CommandTimeout), nameof(options));
        RequireFiniteOrInfinite(options.HeartbeatInterval, nameof(options.HeartbeatInterval), nameof(options));
        RequireFiniteOrInfinite(options.ConnectTimeout, nameof(options.ConnectTimeout), nameof(options));
        if (options.ReconnectAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ReconnectAttempts, "ReconnectAttempts must be at least 1.");
        }

        if (options.UnsolicitedCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.UnsolicitedCapacity, "UnsolicitedCapacity must be at least 1.");
        }

        RequireFinite(options.RebootTimeout, nameof(options.RebootTimeout), nameof(options));
        if (options.ConnectAttempts is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ConnectAttempts, "ConnectAttempts must be at least 1.");
        }

        return new ResilientControlClientOptions
        {
            ResponseTimeout = options.ResponseTimeout,
            LateReplyTimeout = options.LateReplyTimeout,
            CommandTimeout = options.CommandTimeout,
            HeartbeatInterval = options.HeartbeatInterval,
            ConnectTimeout = options.ConnectTimeout,
            ReconnectAttempts = options.ReconnectAttempts,
            UnsolicitedCapacity = options.UnsolicitedCapacity,
            ConnectionRestored = options.ConnectionRestored,
            LoggerFactory = options.LoggerFactory,
            TimeProvider = options.TimeProvider,
            UseJitter = options.UseJitter,
            Rebooter = options.Rebooter,
            RecoveryPolicy = options.RecoveryPolicy,
            RebootTimeout = options.RebootTimeout,
            ConnectAttempts = options.ConnectAttempts,
        };
    }

    /// <param name="name">The option, named in the error.</param>
    /// <param name="paramName">The parameter the options came in.</param>
    private static void RequireFinite(TimeSpan value, string name, string paramName)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{name} must be positive and at most Int32.MaxValue milliseconds.");
        }
    }

    /// <inheritdoc cref="RequireFinite"/>
    private static void RequireFiniteOrInfinite(TimeSpan value, string name, string paramName)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{name} must be positive (at most Int32.MaxValue milliseconds) or Timeout.InfiniteTimeSpan.");
        }
    }

    /// <summary>
    /// The first and only attempt of <c>ConnectAsync</c>: spec 7.4 steps 2-5 with the caller's token, then the
    /// publication and the start of the supervisor.
    /// </summary>
    private async Task<ResilientControlClient> ConnectCoreAsync(CancellationToken ct)
    {
        // Recorded as for a reconnection attempt, so the first attempt after an early loss keeps the 1 s floor.
        _lastAttemptStart = _time.GetTimestamp();
        Link link = await OpenLinkAsync(null, ct).ConfigureAwait(false);
        if (!Publish(link))
        {
            // Nobody holds the client yet, so only ConnectAsync itself could have closed it; kept for the invariant.
            await CloseLinkAsync(link).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(ResilientControlClient));
        }

        _supervisor = SuperviseAsync(link);
        try
        {
            ResilientClientLog.Connected(_logger, link.Client.RemoteEndPoint, link.Client.LocalEndPoint);
        }
        catch
        {
            // A logging provider failed; nothing may be left running when ConnectAsync throws.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return this;
    }

    /// <summary>Replaces the state-change signal; the caller completes the returned one outside the lock. Call under <see cref="_sync"/>.</summary>
    private TaskCompletionSource SwapChanged()
    {
        TaskCompletionSource changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return changed;
    }

    /// <summary>
    /// Spec 7.4 steps 2-5 and 9: a new inner client, the TCP connection within
    /// <see cref="ResilientControlClientOptions.ConnectTimeout"/>, the pump, the handover to <see cref="_restoring"/>
    /// and the verification request. On any failure the inner client is closed and its pump awaited before the
    /// exception leaves.
    /// </summary>
    /// <param name="phase">Told the phase the attempt enters, for event 1104.</param>
    /// <exception cref="ObjectDisposedException">The client was closed while the connection was being made.</exception>
    private async Task<Link> OpenLinkAsync(Action<ReconnectPhase>? phase, CancellationToken ct)
    {
        var inner = new NetSdrControlClient(_innerOptions);
        var link = new Link(inner);
        // Set before the connection exists: the observer must see the first late reply the read loop meets.
        inner.LateReplyObserver = (message, isNak) => OnLateReply(link, message, isNak);
        try
        {
            phase?.Invoke(ReconnectPhase.Connect);
            await ConnectInnerAsync(inner, ct).ConfigureAwait(false);
            // Whatever ends the inner client resolves the exchange it leaves unanswered; the verification is one.
            _ = inner.Completion.ContinueWith(
                _ => OnInnerCompleted(link), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            link.Pump = PumpAsync(link);

            // Step 4. From here DisposeAsync sees the link and closes it; a client already closed takes no new link.
            // The remote end is kept for the next reboot's context (Ruling C23).
            bool closed;
            lock (_sync)
            {
                _lastRemoteEndPoint = inner.RemoteEndPoint ?? _lastRemoteEndPoint;
                closed = _state == ClientState.Closed;
                if (!closed)
                {
                    _restoring = link;
                }
            }

            if (closed)
            {
                throw new ObjectDisposedException(nameof(ResilientControlClient));
            }

            phase?.Invoke(ReconnectPhase.Verify);
            await VerifyAsync(link, ct).ConfigureAwait(false);
            return link;
        }
        catch
        {
            // Step 9.
            await CloseLinkAsync(link).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Ends a link the supervisor will not publish, or has retired: forgets it as the one being restored, closes its
    /// inner client and waits for its pump, so no connection outlives the attempt (spec 6.10 invariant 10).
    /// </summary>
    private async Task CloseLinkAsync(Link link)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_restoring, link))
            {
                _restoring = null;
            }
        }

        await link.Client.DisposeAsync().ConfigureAwait(false);
        await link.Pump.ConfigureAwait(false);
    }

    /// <exception cref="TimeoutException">No TCP connection within <see cref="ResilientControlClientOptions.ConnectTimeout"/>.</exception>
    private async Task ConnectInnerAsync(NetSdrControlClient inner, CancellationToken ct)
    {
        TimeSpan connectTimeout = _options.ConnectTimeout;
        if (connectTimeout == Timeout.InfiniteTimeSpan)
        {
            await _connect(inner, ct).ConfigureAwait(false);
            return;
        }

        // Created with the client's clock and then linked; CancelAfter on the linked source would follow the system clock.
        using var timer = new CancellationTokenSource(connectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            await _connect(inner, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No TCP connection to {_target} within {connectTimeout}.");
        }
    }

    /// <summary>
    /// Proves the device serves this connection with a <c>Get</c> of the status codes: a response or a NAK passes, a
    /// timeout or a loss fails the attempt. Finds a one-client device that still holds a half-open old connection.
    /// </summary>
    private async Task VerifyAsync(Link link, CancellationToken ct)
    {
        await link.Wire.WaitAsync(ct).ConfigureAwait(false);
        Exchange exchange = StartExchange(link, RequestType.Get, StatusCodes.Code, ReadOnlyMemory<byte>.Empty, nameof(StatusCodes));
        try
        {
            await exchange.Request.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (NetSdrNakException)
        {
            // The device is there and answers; it just has no status codes to report.
        }

        // Settle runs apart from the request it settles. Waiting for it here publishes the link with Wire free and
        // LastHeard fresh, so the first heartbeat turn neither finds the line held nor takes the device for silent.
        await exchange.Late.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Spec 6.8: moves everything the inner client publishes to <see cref="Unsolicited"/> until the inner client ends its channel.</summary>
    private async Task PumpAsync(Link link)
    {
        await foreach (ControlItemMessage message in link.Client.Unsolicited.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            link.Heard(_time);
            _unsolicited.Writer.TryWrite(message);
        }
    }

    /// <summary>
    /// The first of spec 6.5 step 0: a command of this client from inside its own
    /// <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback would wait for the connection the callback
    /// is restoring. It is refused synchronously and recorded on the scope, which makes the client give up (spec 7.5).
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside the callback.</exception>
    private void ThrowIfReentrant()
    {
        if (_restoreScope.Value is { Active: true } scope && ReferenceEquals(scope.Owner, this))
        {
            var ex = new InvalidOperationException(
                "Inside ConnectionRestored send requests through context.Client; the ResilientControlClient is waiting for this callback.");
            scope.Reentered ??= ex;
            throw ex;
        }
    }

    /// <summary>Spec 6.5 step 0: the Closed checks, before anything is encoded or queued.</summary>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The client gave up reconnecting.</exception>
    private void ThrowIfClosed()
    {
        if (_state != ClientState.Closed)
        {
            return;
        }

        IOException? failure;
        lock (_sync)
        {
            failure = _failure;
        }

        throw failure is null
            ? new ObjectDisposedException(nameof(ResilientControlClient))
            : new InvalidOperationException("The client gave up reconnecting; create a new client.", failure);
    }

    /// <summary>Spec 6.5 step 0: the payload of a Set, written into a fresh zeroed buffer.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The item does not fit in one frame.</exception>
    private static byte[] Encode<T>(in T item) where T : struct, IControlItem<T>
    {
        int size = T.GetSize(in item);
        NetSdrControlClient.ThrowIfPayloadTooLarge(size, nameof(item));
        var payload = new byte[size];
        T.Write(in item, payload);
        return payload;
    }

    /// <summary>Spec 6.5 step 0 for <see cref="SendAsync"/>: the type first, then the size, then a copy of the payload.</summary>
    private static byte[] CopyPayload(RequestType type, ReadOnlyMemory<byte> payload)
    {
        if (type is not (RequestType.Set or RequestType.Get or RequestType.GetRange))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only Set, Get and GetRange requests can be sent.");
        }

        NetSdrControlClient.ThrowIfPayloadTooLarge(payload.Length, nameof(payload));
        return payload.ToArray();
    }

    /// <summary>The rest of spec 6.5 step 0 for a keyed or keyless request, after the caller's state checks: the size, the copy, the decoding.</summary>
    /// <param name="payloadName">The public parameter the payload comes from, for the size error; <see langword="null"/> when there is none.</param>
    /// <param name="session">The session of a <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback; <see langword="null"/> for a command of the application.</param>
    private Task<T> RequestAsync<T>(
        RequestType type, ReadOnlySpan<byte> payload, string? payloadName, CancellationToken ct, RestoreSession? session)
        where T : struct, IControlItem<T>
    {
        NetSdrControlClient.ThrowIfPayloadTooLarge(payload.Length, payloadName);
        return DecodeAsync<T>(CommandAsync(type, T.Code, payload.ToArray(), typeof(T).Name, ct, session));
    }

    /// <summary>Spec 6.5 step 6: the typed result; a payload that does not read as <typeparamref name="T"/> fails without a retry.</summary>
    private static async Task<T> DecodeAsync<T>(Task<ControlItemMessage> reply) where T : struct, IControlItem<T>
    {
        ControlItemMessage message = await reply.ConfigureAwait(false);
        return ControlItemMessage.ReadItem<T>(message.Payload.Span);
    }

    /// <summary>The last of spec 6.5 step 0: a token already cancelled sends nothing.</summary>
    /// <param name="item">The item name for the logs; <see langword="null"/> for a raw request.</param>
    /// <param name="session">The session of a <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback; <see langword="null"/> for a command of the application.</param>
    private Task<ControlItemMessage> CommandAsync(
        RequestType type, ushort code, ReadOnlyMemory<byte> payload, string? item, CancellationToken ct, RestoreSession? session)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<ControlItemMessage>(ct);
        }

        return RunAsync(new CommandExecution(this, type, code, payload, item) { Session = session }, ct);
    }

    /// <summary>Spec 6.5 steps 1-8: the token, the admission, the retry pipeline and the translation of cancellation.</summary>
    /// <exception cref="TimeoutException">The command did not complete within <see cref="ResilientControlClientOptions.CommandTimeout"/>.</exception>
    private async Task<ControlItemMessage> RunAsync(CommandExecution exec, CancellationToken ct)
    {
        // Step 1. The deadline covers the admission, every attempt and the waits for a connection, on the client's
        // clock (CancelAfter on the linked source would follow the system clock). Infinite means no deadline at all.
        TimeSpan commandTimeout = _options.CommandTimeout;
        using CancellationTokenSource? deadline = commandTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(commandTimeout, _time);
        using CancellationTokenSource linked = deadline is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token, _lifetime.Token);
        CancellationToken token = linked.Token;
        ResilienceContext? context = null;
        bool admitted = false;
        try
        {
            // Step 2. FIFO: commands are written and complete in the order of the calls, and a retry never overtakes.
            // A session's request skips it (spec 6.2): the command waiting for this very restoration may hold it.
            if (exec.Session is null)
            {
                await _admission.WaitAsync(token).ConfigureAwait(false);
                admitted = true;
            }

            // Step 3.
            context = ResilienceContextPool.Shared.Get(token);
            context.Properties.Set(CommandExecution.ExecKey, exec);
            return await _commandPipeline
                .ExecuteAsync(static (c, e) => e.Owner.AttemptAsync(c, e), context, exec)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline is { IsCancellationRequested: true })
        {
            // Step 7. The caller sees OperationCanceledException only for its own token. The deadline covers the
            // time in the queue, behind an unanswered request and waiting for a reconnection alike; the cause is the
            // failure of the last retried attempt, or else why the connection was lost.
            throw new TimeoutException(
                $"{exec.Type} of item 0x{exec.Code:X4} did not complete within {commandTimeout}.", exec.LastError ?? LastLoss());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _lifetime.IsCancellationRequested)
        {
            // Step 7. The end of the client's life becomes the disposal or the failure it gave up with.
            throw ClosedException();
        }
        finally
        {
            // Step 8. The two token sources are disposed by their using declarations.
            if (context is not null)
            {
                ResilienceContextPool.Shared.Return(context);
            }

            if (admitted)
            {
                _admission.Release();
            }
        }
    }

    /// <summary>Why the last published connection was lost, when one was.</summary>
    private Exception? LastLoss()
    {
        lock (_sync)
        {
            return _lastLoss;
        }
    }

    /// <summary>Spec 6.5 step 4: one attempt of a command, on whichever connection is published when it runs.</summary>
    private async ValueTask<ControlItemMessage> AttemptAsync(ResilienceContext context, CommandExecution exec)
    {
        CancellationToken t = context.CancellationToken;

        // Step 4a. The previous attempt timed out on a live connection: wait for that request's late reply instead
        // of writing the request again. The exchange lives on the execution, so a reply that came between the
        // attempts is not lost.
        if (exec.Outstanding is { } outstanding)
        {
            exec.Outstanding = null;
            return await AdoptLateReplyAsync(exec, outstanding, t).ConfigureAwait(false);
        }

        // Step 4b.
        Link link = await WaitForLinkAsync(exec, t).ConfigureAwait(false);

        // Step 4c. Wire is held while an earlier exchange is in flight or unanswered, so getting it means the line is clean.
        await link.Wire.WaitAsync(t).ConfigureAwait(false);
        if (!link.Client.IsConnected)
        {
            link.Wire.Release();
            if (_state == ClientState.Closed)
            {
                // The connection died because the client was closed: the final exception, as in step 4e.
                throw ClosedException();
            }

            throw new IOException($"The connection to {_target} was lost.", LossCauseOf(link));
        }

        // Step 4d. From here Wire belongs to the exchange.
        Exchange exchange = StartExchange(link, exec.Type, exec.Code, exec.Payload, exec.Item);

        // Step 4e.
        try
        {
            return await exchange.Request.WaitAsync(t).ConfigureAwait(false);
        }
        catch (NetSdrNakException)
        {
            // Never retried.
            throw;
        }
        catch (OperationCanceledException) when (t.IsCancellationRequested)
        {
            // A handover: the request stays on the wire and holds Wire until Settle or the observer resolves it, so
            // its late reply can never answer a later request. Never retried: the token is cancelled.
            _ = DrainAsync(exchange);
            throw;
        }
        catch (Exception ex) when (!link.Client.IsConnected)
        {
            if (_state == ClientState.Closed)
            {
                throw ClosedException();
            }

            throw new IOException($"The connection to {_target} was lost.", ex);
        }
        catch (TimeoutException)
        {
            // A busy device: the next attempt adopts the late reply instead of sending the request again.
            exec.Outstanding = exchange;
            throw;
        }

        // A NetSdrProtocolException on a live connection (a foreign reply) leaves as it is and is never retried;
        // the line stays unanswered until the real reply or the deadline.
    }

    /// <summary>
    /// Spec 6.5 step 4e: follows the request of a caller that stopped waiting for it, and writes event 1108 when the
    /// device answers it after all. A lost request is nothing to report: the loss of the connection is. Nobody awaits
    /// this, so a logging provider that throws is not reported either.
    /// </summary>
    private async Task DrainAsync(Exchange exchange)
    {
        Resolution resolution = await exchange.Late.Task.ConfigureAwait(false);
        LogLateReplyDrained(exchange, resolution, LateOwner.CancelledCaller);
    }

    /// <summary>
    /// Event 1108 for an exchange nobody waited for that ended with a late reply or a late NAK; any other resolution
    /// is nothing to report. The exchange is already resolved and the line free, so a logging provider that throws
    /// changes nothing.
    /// </summary>
    private void LogLateReplyDrained(Exchange exchange, Resolution resolution, LateOwner owner)
    {
        if (resolution.Outcome is not (Outcome.Reply or Outcome.Nak))
        {
            return;
        }

        try
        {
            ResilientClientLog.LateReplyDrained(
                _logger, resolution.Outcome == Outcome.Nak ? LateOutcome.Nak : LateOutcome.Reply,
                exchange.Type, exchange.Code, owner);
        }
        catch (Exception)
        {
            // A logging provider failed.
        }
    }

    /// <summary>Spec 6.5 step 4a: the outcome of the exchange the previous attempt left unanswered.</summary>
    /// <exception cref="NetSdrNakException">The device answered the request late with a NAK.</exception>
    /// <exception cref="IOException">The connection was lost before the request was answered; retried on the next connection.</exception>
    private async ValueTask<ControlItemMessage> AdoptLateReplyAsync(CommandExecution exec, Exchange outstanding, CancellationToken t)
    {
        Resolution late;
        try
        {
            late = await outstanding.Late.Task.WaitAsync(t).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (t.IsCancellationRequested)
        {
            // The caller stopped waiting for a request that is still on the line: the same handover as in step 4e.
            _ = DrainAsync(outstanding);
            throw;
        }

        switch (late.Outcome)
        {
            case Outcome.Reply:
                LogLateReplyAdopted(exec, outstanding, LateOutcome.Reply);
                return late.Message;
            case Outcome.Nak:
                LogLateReplyAdopted(exec, outstanding, LateOutcome.Nak);
                throw new NetSdrNakException(exec.Code, exec.Type);
            default:
                // Lost. Answered is impossible: the inner request had already failed when the exchange became outstanding.
                if (_state == ClientState.Closed)
                {
                    // The connection died because the client was closed: the final exception, as in step 4e.
                    throw ClosedException();
                }

                throw new IOException(
                    $"The connection to {_target} was lost before {exec.Type} 0x{exec.Code:X4} was answered.", late.Cause);
        }
    }

    /// <summary>
    /// Event 1107; <c>Late</c> is how long after its response timeout the request was answered. The exchange is
    /// already resolved, so a logging provider that throws does not turn an answered command into a failure.
    /// </summary>
    private void LogLateReplyAdopted(CommandExecution exec, Exchange exchange, LateOutcome outcome)
    {
        TimeSpan late = _time.GetElapsedTime(exchange.SentAt, exchange.ResolvedAt) - _options.ResponseTimeout;
        try
        {
            ResilientClientLog.LateReplyAdopted(
                _logger, exec.Type, exec.Item ?? "raw", exec.Code, outcome, late < TimeSpan.Zero ? TimeSpan.Zero : late);
        }
        catch (Exception)
        {
            // A logging provider failed.
        }
    }

    /// <summary>
    /// Spec 6.5 step 4b: the published connection, once it is alive. Waits through a reconnection on the state-change
    /// signal; Closed ends the wait with the final exception. A session's request is bound to its own connection and
    /// never waits for another: a dead one fails it with <see cref="IOException"/>, an ended session with
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    private async ValueTask<Link> WaitForLinkAsync(CommandExecution exec, CancellationToken t)
    {
        if (exec.Session is { } session)
        {
            session.ThrowIfEnded();
            if (_state == ClientState.Closed)
            {
                throw ClosedException();
            }

            Link bound = session.Link;
            if (!bound.Client.IsConnected)
            {
                throw new IOException($"The connection to {_target} was lost.", LossCauseOf(bound));
            }

            return bound;
        }

        while (true)
        {
            Task changed;
            bool closed;
            lock (_sync)
            {
                if (_state == ClientState.Connected && _link.Client.IsConnected)
                {
                    return _link;
                }

                // The state and the signal are read in one critical section, so no transition is missed.
                closed = _state == ClientState.Closed;
                changed = _changed.Task;
            }

            if (closed)
            {
                throw ClosedException();
            }

            await changed.WaitAsync(t).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spec 6.5 step 4d: makes a new exchange the current one of <paramref name="link"/> and writes its request through
    /// the inner client with a token that never cancels. The caller holds Wire, which belongs to the exchange from now
    /// on and is released only by <see cref="Resolve"/>.
    /// </summary>
    private Exchange StartExchange(Link link, RequestType type, ushort code, ReadOnlyMemory<byte> payload, string? item)
    {
        var exchange = new Exchange(link, type, code, item, _time.GetTimestamp());
        lock (_sync)
        {
            link.Current = exchange;
        }

        Task<ControlItemMessage> request;
        try
        {
            request = link.Client.SendAsync(type, code, payload, item, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Only invalid arguments throw synchronously, and they were checked before; the exchange still owns Wire.
            request = Task.FromException<ControlItemMessage>(ex);
        }

        exchange.Request = request;
        _ = request.ContinueWith(
            _settle, exchange, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        return exchange;
    }

    /// <summary>
    /// Spec 6.6: runs when the inner request completed. A response or a NAK answered the exchange. A timeout or a
    /// foreign reply on a live connection leaves it unanswered: Wire stays held and <see cref="Expire"/> is scheduled
    /// for <c>ResponseTimeout + LateReplyTimeout</c> after the write, unless the observer resolved it meanwhile.
    /// Anything else, or a dead connection, resolves it as lost. Nothing is logged here; whoever adds a logger call
    /// puts it in a try whose finally resolves the exchange, so Wire is released whatever happens.
    /// </summary>
    private void Settle(Exchange exchange)
    {
        Task<ControlItemMessage> request = exchange.Request;
        Link link = exchange.Link;
        if (request.IsCompletedSuccessfully)
        {
            link.Heard(_time);
            Resolve(exchange, new Resolution(Outcome.Answered, request.Result));
            return;
        }

        // Reading Exception marks it observed.
        Exception cause = request.Exception?.InnerException ?? (Exception?)request.Exception ?? new TaskCanceledException(request);
        if (cause is NetSdrNakException)
        {
            link.Heard(_time);
            Resolve(exchange, new Resolution(Outcome.Answered));
            return;
        }

        if (cause is not (TimeoutException or NetSdrProtocolException) || !link.Client.IsConnected)
        {
            Resolve(exchange, new Resolution(Outcome.Lost, Cause: cause));
            return;
        }

        if (cause is NetSdrProtocolException)
        {
            // A foreign reply is still a frame from the device.
            link.Heard(_time);
        }

        TimeSpan due = _options.ResponseTimeout + _options.LateReplyTimeout - _time.GetElapsedTime(exchange.SentAt);
        lock (_sync)
        {
            if (exchange.State != ExchangeState.InFlight)
            {
                // The late reply came before the inner client gave up on it, and the observer resolved the exchange.
                return;
            }

            exchange.State = ExchangeState.Unanswered;
        }

        // Created outside the lock: a TimeProvider may run an already-due timer inline, and Expire logs and closes
        // the connection, which nothing does under _sync (spec 6.10 invariant 12).
        ITimer deadline = _time.CreateTimer(
            Expire, exchange, due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        bool resolved;
        lock (_sync)
        {
            resolved = exchange.State == ExchangeState.Resolved;
            if (!resolved)
            {
                exchange.Deadline = deadline;
            }
        }

        if (resolved)
        {
            // The late reply, the loss, or the timer itself resolved the exchange meanwhile: Resolve found no timer to free.
            deadline.Dispose();
        }
    }

    /// <summary>
    /// Spec 6.6: the inner client's <see cref="NetSdrControlClient.LateReplyObserver"/>, called from its read loop
    /// without its lock and before the message is published. A late reply or a late NAK answers the unresolved
    /// exchange on the line. Never blocks and never throws: it only completes a <see cref="TaskCompletionSource{T}"/>
    /// that runs its continuations asynchronously and releases Wire.
    /// </summary>
    private void OnLateReply(Link link, ControlItemMessage message, bool isNak)
    {
        Exchange? exchange;
        lock (_sync)
        {
            exchange = link.Current;
        }

        if (exchange is null)
        {
            return;
        }

        // Heard before the resolution: the heartbeat that wakes on the resolved exchange reads LastHeard next, and
        // must not send a probe right after the device was heard. A duplicate resolution leaves it refreshed, which
        // is as true: a frame did arrive.
        link.Heard(_time);
        Resolve(exchange, isNak ? new Resolution(Outcome.Nak) : new Resolution(Outcome.Reply, message));
    }

    /// <summary>
    /// Spec 6.6: the late-reply deadline of an unanswered exchange passed. If it is still unanswered on a live
    /// connection, the connection is unresponsive: it is closed and the exchange is lost. After the exchange was
    /// resolved, or once the connection is dead, there is nothing to do.
    /// </summary>
    /// <param name="state">The <see cref="Exchange"/> the timer belongs to.</param>
    private void Expire(object? state)
    {
        var exchange = (Exchange)state!;
        Link link = exchange.Link;
        var cause = new TimeoutException(
            $"No reply to {exchange.Type} 0x{exchange.Code:X4} within {_options.ResponseTimeout + _options.LateReplyTimeout}.");
        lock (_sync)
        {
            if (exchange.State != ExchangeState.Unanswered || !link.Client.IsConnected)
            {
                return;
            }

            link.LossCause = cause;
        }

        try
        {
            ResilientClientLog.ConnectionUnresponsive(
                _logger, exchange.Type, exchange.Code, _time.GetElapsedTime(exchange.SentAt), link.Client.RemoteEndPoint);
        }
        finally
        {
            // A logging provider that throws must not leave the line held or the connection open. The synchronous
            // part of the inner client's disposal makes IsConnected false at once; the supervisor awaits the rest.
            _ = link.Client.DisposeAsync();
            Resolve(exchange, new Resolution(Outcome.Lost, Cause: cause));
        }
    }

    /// <summary>
    /// Runs when the inner client of <paramref name="link"/> ended, whatever the reason: the exchange it leaves on
    /// the line can no longer be answered, so it is lost with the cause of the loss.
    /// </summary>
    private void OnInnerCompleted(Link link)
    {
        Exchange? exchange;
        lock (_sync)
        {
            exchange = link.Current;
        }

        if (exchange is not null)
        {
            Resolve(exchange, new Resolution(Outcome.Lost, Cause: LossCauseOf(link)));
        }
    }

    /// <summary>
    /// Resolves an exchange exactly once: the state changes under <see cref="_sync"/>, then Wire is released and
    /// <see cref="Exchange.Late"/> completes outside it. Returns <see langword="true"/> only for the call that resolved it.
    /// </summary>
    private bool Resolve(Exchange exchange, Resolution resolution)
    {
        ITimer? deadline;
        lock (_sync)
        {
            if (exchange.State == ExchangeState.Resolved)
            {
                return false;
            }

            exchange.State = ExchangeState.Resolved;
            exchange.ResolvedAt = _time.GetTimestamp();
            deadline = exchange.Deadline;
            exchange.Deadline = null;
            if (ReferenceEquals(exchange.Link.Current, exchange))
            {
                exchange.Link.Current = null;
            }
        }

        deadline?.Dispose();
        try
        {
            // The line first: whoever Late wakes (the heartbeat, an adoption, a drain) never finds it still held.
            exchange.Link.Wire.Release();
        }
        finally
        {
            exchange.Late.TrySetResult(resolution);
        }

        return true;
    }

    /// <summary>Spec 6.5 step 5 and spec 8: whether a failed attempt gets another one.</summary>
    private static bool ShouldRetryCommand(ResilienceContext context, Exception? exception)
    {
        // Checked first: Polly asks before its own cancellation check and before OnRetry, which would otherwise log a retry.
        if (context.CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        CommandExecution exec = context.Properties.GetValue(CommandExecution.ExecKey, null!);
        if (exec.Owner._state == ClientState.Closed)
        {
            return false;
        }

        return exception switch
        {
            IOException => exec.Bound is null,                  // a session's requests stay on their connection
            TimeoutException => exec.Outstanding is not null,   // the next attempt adopts the late reply
            _ => false,
        };
    }

    private void LogCommandRetrying(CommandExecution exec, int attempt, Exception exception) =>
        ResilientClientLog.CommandRetrying(
            _logger, exec.Type, exec.Item ?? "raw", exec.Code, attempt,
            exception is TimeoutException ? "NoReplyWaitingForLateReply" : "ConnectionLost", exception);

    /// <summary>What a request learns when the client is Closed: the disposal, or the failure the client gave up with.</summary>
    private Exception ClosedException()
    {
        IOException? failure;
        lock (_sync)
        {
            failure = _failure;
        }

        return (Exception?)failure ?? new ObjectDisposedException(nameof(ResilientControlClient));
    }

    /// <summary>Why a link's inner client is no longer connected, when it has said so.</summary>
    private static Exception? LossCauseOf(Link link)
    {
        if (link.LossCause is { } cause)
        {
            return cause;
        }

        Task completion = link.Client.Completion;
        return completion.IsFaulted ? completion.Exception!.InnerException : null;
    }
}
