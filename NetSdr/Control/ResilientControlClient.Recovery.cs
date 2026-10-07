using System.Net;
using System.Runtime.ExceptionServices;
using Polly;

namespace NetSdr.Control;

/// <summary>
/// Device reboots (reboot spec 4 and 5): the slot of a manual <c>RebootAsync</c> request and the types that carry a
/// reboot decision out of an attempt series.
/// </summary>
public sealed partial class ResilientControlClient
{
    /// <summary>
    /// Wakes a watch or an attempt series when a manual reboot request is registered; replaced whenever a request is
    /// taken. Guarded by <see cref="_sync"/>.
    /// </summary>
    private CancellationTokenSource _rebootWake = new();

    /// <summary>The manual reboot request waiting for the supervisor, or running; <see langword="null"/> when there is none. Guarded by <see cref="_sync"/>.</summary>
    private RebootRequest? _rebootRequest;

    /// <summary>
    /// The remote end of the most recent attempt that established TCP (Ruling C23), for <see cref="RebootContext.LastRemoteEndPoint"/>;
    /// <see langword="null"/> while no attempt has had TCP. Guarded by <see cref="_sync"/>.
    /// </summary>
    private IPEndPoint? _lastRemoteEndPoint;

    /// <summary>
    /// Spec 5.2: takes the request in the slot, if there is one, and marks it running; it stays in the slot, so later
    /// requests join it. With an <paramref name="escalation"/> and an empty slot a running placeholder takes the slot
    /// for the length of the reboot step, so a manual request joins the escalation's reboot too (Ruling C11); a request
    /// already in the slot takes the escalation's kind when it is Hard. Always replaces the wake, so the next series or
    /// watch is not woken by the request just taken.
    /// </summary>
    private RebootRequest? TakeRebootRequest(RebootKind? escalation = null)
    {
        RebootRequest? request;
        CancellationTokenSource previous;
        lock (_sync)
        {
            request = _rebootRequest;
            if (request is not null)
            {
                request.Running = true;
                if (escalation == RebootKind.Hard)
                {
                    request.Kind = RebootKind.Hard;
                }
            }
            else if (escalation is { } kind)
            {
                request = _rebootRequest = new RebootRequest { Kind = kind, Running = true, Escalation = true };
            }

            previous = _rebootWake;
            _rebootWake = new CancellationTokenSource();
        }

        previous.Dispose();
        return request;
    }

    /// <summary>
    /// Spec 5.2 and the merge rule of 4.2: an empty slot gets a new request, a request that has not started merges with
    /// this one (Hard wins), and a running one is joined unchanged. Only the first two wake the supervisor. Returns the
    /// task every caller of the request shares.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The client was disposed after the caller's Closed check (Ruling C12).</exception>
    /// <exception cref="InvalidOperationException">The client gave up after the caller's Closed check; the cause is the inner exception.</exception>
    private Task RegisterRebootRequest(RebootKind kind)
    {
        RebootRequest request;
        CancellationTokenSource? wake = null;
        lock (_sync)
        {
            // Ruling C12: checked in the section that writes the slot, which DisposeAsync and GiveUp empty under the
            // same lock, so no request is left in a slot nobody completes.
            if (_state == ClientState.Closed)
            {
                throw _failure is null
                    ? new ObjectDisposedException(nameof(ResilientControlClient))
                    : new InvalidOperationException("The client gave up reconnecting; create a new client.", _failure);
            }

            if (_rebootRequest is null)
            {
                request = _rebootRequest = new RebootRequest { Kind = kind };
                wake = _rebootWake;
            }
            else
            {
                request = _rebootRequest;
                if (!request.Running)
                {
                    if (kind == RebootKind.Hard)
                    {
                        request.Kind = RebootKind.Hard;
                    }

                    wake = _rebootWake;
                }
            }
        }

        if (wake is not null)
        {
            try
            {
                wake.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ruling C8: TakeRebootRequest replaced and disposed this wake meanwhile, so the request is already taken.
            }
        }

        return request.Completion.Task;
    }

    /// <summary>
    /// Takes the request out of the slot and empties it, for <see cref="DisposeAsync"/> and <see cref="GiveUp"/>, which
    /// fail it outside the lock. They call it inside their own <see cref="_sync"/> section, the one that closes the client.
    /// </summary>
    private RebootRequest? DropRebootRequest()
    {
        lock (_sync)
        {
            RebootRequest? request = _rebootRequest;
            _rebootRequest = null;
            return request;
        }
    }

    /// <summary>The token that a manual reboot request cancels to wake the supervisor.</summary>
    private CancellationToken CurrentWake()
    {
        lock (_sync)
        {
            return _rebootWake.Token;
        }
    }

    /// <summary>Empties the slot when it still holds <paramref name="request"/>.</summary>
    private void ClearRebootRequest(RebootRequest request)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_rebootRequest, request))
            {
                _rebootRequest = null;
            }
        }
    }

    /// <summary>
    /// The start of every attempt, of a loss or of the first connection: the attempt limit across every series (reboot
    /// spec 5.3, Ruling C10), checked first so the policy is never asked about an attempt that does not happen; then
    /// the 1 s floor since the previous attempt started (Polly's jittered delay can be nearly zero, and the first
    /// attempt after an early loss keeps it too), which <paramref name="ct"/> ends; then the attempt is counted.
    /// </summary>
    private async ValueTask BeginAttemptAsync(ReconnectState state, int attempts, CancellationToken ct)
    {
        if (state.Attempt >= attempts)
        {
            ExceptionDispatchInfo.Throw(state.LastFailure!);
        }

        TimeSpan remaining = AttemptFloor - _time.GetElapsedTime(_lastAttemptStart, _time.GetTimestamp());
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _time, ct).ConfigureAwait(false);
        }

        state.Attempt++;
        _lastAttemptStart = _time.GetTimestamp();
    }

    /// <summary>
    /// Reboot spec 4.5: one attempt of the first connection, spec 7.4 steps 2-5 with the caller's token and no
    /// <see cref="ReconnectPhase.Restore"/> phase; <see cref="OpenLinkAsync"/> closes the link of a failed attempt
    /// itself. The caller's cancellation leaves as it is; any other failure goes to the recovery policy, without the
    /// reboot slot.
    /// </summary>
    private async ValueTask<Link> ConnectOnceAsync(ResilienceContext context, ReconnectState state)
    {
        CancellationToken ct = context.CancellationToken;
        await BeginAttemptAsync(state, _connectAttempts, ct).ConfigureAwait(false);
        try
        {
            return await OpenLinkAsync(phase => state.Phase = phase, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Exception decided = DecideAfterFailure(state, ex, checkSlot: false);
            if (ReferenceEquals(decided, ex))
            {
                throw;
            }

            throw decided;
        }
    }

    /// <summary>
    /// Spec 5.3: what a failed attempt ends with. Without a rebooter it is the failure itself. With one, a request in
    /// the slot (when <paramref name="checkSlot"/>) schedules its reboot without asking the policy; otherwise the
    /// policy decides: a reboot ends the series with <see cref="RebootScheduledException"/>, a give-up with
    /// <see cref="RecoveryGaveUpException"/>, and Continue or a failing policy (event 1117) return the failure.
    /// Called outside <see cref="_sync"/>.
    /// </summary>
    private Exception DecideAfterFailure(ReconnectState state, Exception failure, bool checkSlot)
    {
        state.LastFailure = failure;
        if (_policy is null)
        {
            return failure;
        }

        if (checkSlot)
        {
            RebootKind? requested;
            lock (_sync)
            {
                requested = _rebootRequest?.Kind;
            }

            if (requested is { } kind)
            {
                return new RebootScheduledException(kind, failure);
            }
        }

        state.FailedAttemptsSinceReboot++;
        RecoveryAction action;
        try
        {
            action = _policy.OnAttemptFailed(new RecoveryContext(
                state.Attempt, state.FailedAttemptsSinceReboot, state.Phase, failure,
                _time.GetElapsedTime(state.LostTimestamp), state.SoftReboots, state.HardReboots));
        }
        catch (Exception ex)
        {
            try
            {
                ResilientClientLog.RecoveryPolicyFailed(_logger, ex);
            }
            catch (Exception)
            {
                // A logging provider failed; a failing policy still means Continue.
            }

            return failure;
        }

        return action.Kind switch
        {
            RecoveryActionKind.Reboot => new RebootScheduledException(action.RebootKind, failure),
            RecoveryActionKind.GiveUp => new RecoveryGaveUpException(failure),
            _ => failure,
        };
    }

    /// <summary>
    /// Spec 4.3: one reboot, for an escalation (<paramref name="request"/> is <see langword="null"/> or a placeholder,
    /// Ruling C11) or a manual request; the callers of <paramref name="request"/> fail with a failed reboot, and wait for
    /// the next <c>Publish</c> after an accepted one.
    /// The transport call is bounded by <see cref="ResilientControlClientOptions.RebootTimeout"/>; it and the boot wait
    /// end with <paramref name="ct"/>, whose cancellation leaves as <see cref="OperationCanceledException"/>. A failed
    /// reboot (the transport, its timeout, or an unusable boot time) is counted, written as 1115, and returns at once.
    /// </summary>
    /// <param name="lastFailure">The failure that led to an escalation, for event 1114.</param>
    /// <param name="ct">The lifetime of the client, or the caller's token for the first connection.</param>
    private async Task RebootStepAsync(
        RebootKind kind, ReconnectState state, RebootRequest? request, Exception? lastFailure, CancellationToken ct)
    {
        // Step 1.
        bool manual = request is { Escalation: false };
        if (!manual)
        {
            try
            {
                ResilientClientLog.RebootEscalated(_logger, kind, _target, state.Attempt, state.Phase, lastFailure!);
            }
            catch (Exception)
            {
                // A logging provider failed; the reboot still runs.
            }
        }

        // Step 2.
        IPEndPoint? lastRemote;
        lock (_sync)
        {
            lastRemote = _lastRemoteEndPoint ?? _link?.Client.RemoteEndPoint;
        }

        var context = new RebootContext(_target, lastRemote, manual);
        Exception? failure = null;
        using (var timer = new CancellationTokenSource(_options.RebootTimeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token))
        {
            try
            {
                Task reboot = _rebooter!.RebootAsync(kind, context, linked.Token);
                // A transport that ignores its token is left behind on a timeout or a disposal (Ruling 10); its fault is observed.
                _ = reboot.ContinueWith(
                    static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await reboot.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested)
            {
                failure = new TimeoutException($"The {kind} reboot of {_target} did not complete within {_options.RebootTimeout}.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        // Step 3: counted whether or not the transport succeeded, so the ladder can go on to a hard reboot.
        if (kind == RebootKind.Hard)
        {
            state.HardReboots++;
        }
        else
        {
            state.SoftReboots++;
        }

        state.FailedAttemptsSinceReboot = 0;

        // Step 5. An unusable boot time is a failed reboot (Ruling 7): Task.Delay would throw past Int32.MaxValue ms.
        TimeSpan bootTime = TimeSpan.Zero;
        if (failure is null)
        {
            try
            {
                bootTime = _rebooter!.GetBootTime(kind);
                if (bootTime < TimeSpan.Zero || bootTime.TotalMilliseconds > int.MaxValue)
                {
                    failure = new InvalidOperationException(
                        $"GetBootTime({kind}) returned {bootTime}, which is not between zero and Int32.MaxValue milliseconds.");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        // Step 4.
        if (failure is not null)
        {
            try
            {
                ResilientClientLog.RebootFailed(_logger, kind, _target, failure);
            }
            catch (Exception)
            {
                // A logging provider failed; a new series of attempts follows.
            }

            if (request is not null)
            {
                Fail(request, failure);
                ClearRebootRequest(request);
            }

            return;
        }

        try
        {
            ResilientClientLog.RebootAccepted(_logger, _target, kind, bootTime);
        }
        catch (Exception)
        {
            // A logging provider failed; the boot wait still runs.
        }

        state.AfterReboot = kind;

        // Step 6. The 1 s floor between attempt starts is kept by the first attempt of the next series.
        await Task.Delay(bootTime, _time, ct).ConfigureAwait(false);
        if (request is not null)
        {
            ClearRebootRequest(request);
            state.ManualWaiters.Add(request);
        }
    }

    /// <summary>Fails the callers of <paramref name="request"/>; the failure is observed, as nobody may be waiting for it.</summary>
    private static void Fail(RebootRequest request, Exception failure)
    {
        if (request.Completion.TrySetException(failure))
        {
            _ = request.Completion.Task.Exception;
        }
    }

    /// <summary>A manual reboot request: its kind (Hard wins when requests merge) and the callers waiting for it.</summary>
    private sealed class RebootRequest
    {
        public RebootKind Kind { get; set; }

        /// <summary>Completes once the client is connected again after the reboot, or fails with what failed it.</summary>
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the reboot step has started; a request that runs is joined, not merged.</summary>
        public bool Running { get; set; }

        /// <summary>Whether this is the placeholder of an escalation's reboot, which manual requests join (Ruling C11).</summary>
        public bool Escalation { get; init; }
    }

    /// <summary>Ends an attempt series because the recovery policy decided on a reboot; never leaves the client.</summary>
    private sealed class RebootScheduledException(RebootKind kind, Exception failure) : Exception("A reboot was scheduled.", failure)
    {
        public RebootKind Kind { get; } = kind;
    }

    /// <summary>Ends an attempt series because the recovery policy gave up; never leaves the client.</summary>
    private sealed class RecoveryGaveUpException(Exception failure) : Exception("The recovery policy gave up.", failure);
}
