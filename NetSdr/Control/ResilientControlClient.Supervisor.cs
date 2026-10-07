using System.Runtime.ExceptionServices;
using NetSdr.Framing;
using NetSdr.Items;
using Polly;

namespace NetSdr.Control;

/// <summary>
/// The supervisor's side of the client (spec 7): one task per client that notices the loss of the published connection,
/// reconnects with a backoff, publishes the new connection or gives up, and the end of the client's life (spec 7.7).
/// The heartbeat joins here.
/// </summary>
public sealed partial class ResilientControlClient
{
    /// <summary>The least time between the starts of two connection attempts, whatever the backoff says (spec 7.4 step 1).</summary>
    private static readonly TimeSpan AttemptFloor = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Where the reconnection pipeline finds the state of the current loss: <c>OnRetry</c> gets no <c>TState</c>, and
    /// event 1104 must name the same attempt as 1105 and 1106.
    /// </summary>
    private static readonly ResiliencePropertyKey<ReconnectState> ReconnectKey = new("NetSdr.Reconnect");

    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Started by <c>ConnectAsync</c> once the first connection is published; never faults, and <see cref="DisposeAsync"/>
    /// waits for it, unless it is called from inside the <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback.
    /// </summary>
    private Task _supervisor = Task.CompletedTask;

    /// <summary>
    /// Closes the connection and ends the client. Requests in flight, queued or made later fail with
    /// <see cref="ObjectDisposedException"/>; <see cref="Completion"/> completes successfully, unless the client had
    /// already given up, and then <see cref="Unsolicited"/> ends. Idempotent, and never throws. Called from inside the
    /// <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback it starts the closing and returns without
    /// waiting for the supervisor, which is waiting for that callback.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource changed;
        Link link;
        Link? restoring;
        lock (_sync)
        {
            if (_disposed)
            {
                return new ValueTask(_disposal.Task);
            }

            // Step 1. After a give-up the state is already Closed and _failure keeps the cause; only the flag is new.
            _disposed = true;
            _state = ClientState.Closed;
            changed = SwapChanged();
            link = _link;
            restoring = _restoring;
        }

        changed.TrySetResult();
        bool fromCallback = _restoreScope.Value is { Active: true } scope && ReferenceEquals(scope.Owner, this);
        return new ValueTask(DisposeCoreAsync(link, restoring, waitForSupervisor: !fromCallback));
    }

    /// <param name="link">The published connection.</param>
    /// <param name="restoring">The connection of the reconnection attempt in progress, if one was.</param>
    /// <param name="waitForSupervisor">Whether step 4 runs: <see langword="false"/> from inside the callback the supervisor is waiting for (spec 7.5).</param>
    private async Task DisposeCoreAsync(Link link, Link? restoring, bool waitForSupervisor)
    {
        try
        {
            // Step 2. Stops every wait on the lifetime token: the backoff, the connection attempt, the 1 s floor, and
            // the queued commands and waits for a connection, which end with ObjectDisposedException.
            _lifetime.Cancel();

            // Step 3. A request in flight on either fails, and its caller sees ObjectDisposedException.
            await link.Client.DisposeAsync().ConfigureAwait(false);
            await link.Pump.ConfigureAwait(false);
            if (restoring is not null)
            {
                await restoring.Client.DisposeAsync().ConfigureAwait(false);
                await restoring.Pump.ConfigureAwait(false);
            }

            // Step 4. After this every pump has ended and no timer does anything.
            if (waitForSupervisor)
            {
                await _supervisor.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The inner clients' disposal, the pumps and the supervisor never throw; nothing else is left to clean up.
        }
        finally
        {
            // Step 5. Completion first, so whoever sees Unsolicited end finds its outcome already there. A give-up failure is kept.
            _completion.TrySetResult();
            try
            {
                // Step 6.
                ResilientClientLog.Disposed(_logger, _target);
            }
            catch (Exception)
            {
                // A logging provider failed; the disposal still completes.
            }
            finally
            {
                // Step 7.
                _unsolicited.Writer.TryComplete();
                _disposal.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Spec 7.1: watches the published connection, and when it ends marks the loss, retires it, reconnects through the
    /// backoff pipeline and publishes the new connection, for as long as the client lives. Never throws: the disposal
    /// ends it quietly, and any other failure of the pipeline makes the client give up.
    /// </summary>
    private async Task SuperviseAsync(Link link)
    {
        ReconnectState? state = null;
        try
        {
            while (true)
            {
                await WatchAsync(link).ConfigureAwait(false);
                Exception cause = link.LossCause
                    ?? link.Client.Completion.Exception?.InnerException
                    ?? new IOException("Connection closed.");
                DateTimeOffset lostAt = _time.GetUtcNow();
                long lostTimestamp = _time.GetTimestamp();
                MarkLost(link, cause);
                ResilientClientLog.ConnectionLost(_logger, link.Client.RemoteEndPoint, cause);

                // The old connection is fully drained before the next one is made (spec 6.10 invariant 10).
                await link.Client.DisposeAsync().ConfigureAwait(false);
                await link.Pump.ConfigureAwait(false);

                state = new ReconnectState(cause, lostAt, lostTimestamp);
                ResilienceContext context = ResilienceContextPool.Shared.Get(_lifetime.Token);
                try
                {
                    context.Properties.Set(ReconnectKey, state);
                    link = await _reconnect.ExecuteAsync(ReconnectOnceAsync, context, state).ConfigureAwait(false);
                }
                finally
                {
                    ResilienceContextPool.Shared.Return(context);
                }

                if (!Publish(link))
                {
                    // Closed meanwhile: the connection is not wanted. Closed outside the lock, as Publish promises.
                    await CloseLinkAsync(link).ConfigureAwait(false);
                    return;
                }

                ResilientClientLog.Reconnected(
                    _logger, link.Client.RemoteEndPoint, link.Client.LocalEndPoint, state.Attempt,
                    _time.GetElapsedTime(state.LostTimestamp, _time.GetTimestamp()));
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // DisposeAsync ended a wait, or the client already gave up; never reported as a give-up.
        }
        catch (Exception ex)
        {
            // The pipeline ran out of attempts, or an attempt failed for good.
            GiveUp(ex, state?.Attempt ?? 0);
        }
    }

    /// <summary>
    /// Spec 7.3: probes the published connection with a <c>Get</c> of the status codes whenever nothing has been
    /// heard from the device for <see cref="ResilientControlClientOptions.HeartbeatInterval"/>, and returns when the
    /// inner client of <paramref name="link"/> has ended, for whatever reason, without throwing: the supervisor reads
    /// the cause from the client itself. A dead idle connection is found by the heartbeat's late-reply deadline
    /// (<see cref="Expire"/>), which closes the inner client.
    /// </summary>
    private async Task WatchAsync(Link link)
    {
        Task completion = link.Client.Completion;
        try
        {
            while (!completion.IsCompleted && !_lifetime.IsCancellationRequested)
            {
                await HeartbeatTurnAsync(link, completion).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A logging provider failed, or the disposal cancelled a wait: the heartbeat stops, the end of the
            // connection is still awaited below, and the exchange on the line is resolved by Settle, the observer,
            // Expire or the end of the inner client, as any other.
        }

        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The fault is the loss; SuperviseAsync takes it from Completion.
        }
    }

    /// <summary>
    /// One turn of the heartbeat loop: waits until the heartbeat is due, skips it while a request of somebody else is
    /// on the line, which is the probe then, and otherwise sends one and waits for its outcome. Every wait ends when
    /// <paramref name="completion"/> completes or the client is disposed.
    /// </summary>
    private async Task HeartbeatTurnAsync(Link link, Task completion)
    {
        TimeSpan interval = _options.HeartbeatInterval;
        if (interval == Timeout.InfiniteTimeSpan)
        {
            // No heartbeat: only the end of the connection matters.
            await Task.WhenAny(completion).ConfigureAwait(false);
            return;
        }

        // LastHeard is written by every reply, NAK, foreign reply, late reply and pumped message of the connection.
        TimeSpan untilDue = interval - _time.GetElapsedTime(Volatile.Read(ref link.LastHeard));
        if (untilDue > TimeSpan.Zero)
        {
            await Task.WhenAny(completion, Task.Delay(untilDue, _time, _lifetime.Token)).ConfigureAwait(false);
            return;
        }

        // A non-blocking try never overtakes a queued command, and fails while a request is in flight or unanswered:
        // checking and taking the line is one atomic step.
        if (!link.Wire.Wait(0))
        {
            await Task.WhenAny(completion, Task.Delay(interval, _time, _lifetime.Token)).ConfigureAwait(false);
            return;
        }

        // From here Wire belongs to the exchange, until it is resolved.
        Exchange exchange = StartExchange(link, RequestType.Get, StatusCodes.Code, ReadOnlyMemory<byte>.Empty, nameof(StatusCodes));
        try
        {
            // Bounded by the inner client's ResponseTimeout, on the same clock (spec 6.7).
            await exchange.Request.ConfigureAwait(false);
        }
        catch (TimeoutException) when (link.Client.IsConnected)
        {
            // Unanswered on a live connection: the late reply resolves the exchange, or Expire closes the connection.
            ResilientClientLog.HeartbeatMissed(_logger, _options.ResponseTimeout, _options.LateReplyTimeout);
        }
        catch (Exception)
        {
            // A NAK proves the device alive. A foreign reply leaves the exchange unanswered with the same deadline.
            // Anything else is a dead connection, which Completion reports.
        }

        // The next heartbeat is not sent while this one is unanswered: the line is held until the exchange is
        // resolved, and its resolution is what frees the line and updates LastHeard.
        Resolution resolution = await exchange.Late.Task.ConfigureAwait(false);
        if (resolution.Outcome is Outcome.Reply or Outcome.Nak)
        {
            ResilientClientLog.LateReplyDrained(
                _logger, resolution.Outcome == Outcome.Nak ? LateOutcome.Nak : LateOutcome.Reply,
                exchange.Type, exchange.Code, LateOwner.Heartbeat);
        }
    }

    /// <summary>
    /// Spec 6.1 <c>MarkLost</c>: the published connection is gone. Only while Connected and only for the published
    /// link: the state becomes Reconnecting, the cause is kept for the commands that time out meanwhile, and the exchange
    /// on the line is lost, which frees Wire.
    /// </summary>
    private void MarkLost(Link link, Exception cause)
    {
        TaskCompletionSource changed;
        Exchange? exchange;
        lock (_sync)
        {
            if (_state != ClientState.Connected || !ReferenceEquals(_link, link))
            {
                return;
            }

            _state = ClientState.Reconnecting;
            _lastLoss = cause;
            exchange = link.Current;
            changed = SwapChanged();
        }

        changed.TrySetResult();
        if (exchange is not null)
        {
            Resolve(exchange, new Resolution(Outcome.Lost, Cause: cause));
        }
    }

    /// <summary>
    /// Spec 6.1 <c>Publish</c>: makes <paramref name="link"/> the connection commands use and forgets it as the one
    /// being restored. Returns <see langword="false"/> when the client is Closed: the link is not published, and the
    /// caller closes it outside the lock.
    /// </summary>
    private bool Publish(Link link)
    {
        TaskCompletionSource changed;
        lock (_sync)
        {
            _restoring = null;
            if (_state == ClientState.Closed)
            {
                return false;
            }

            _link = link;
            _state = ClientState.Connected;
            changed = SwapChanged();
        }

        changed.TrySetResult();
        return true;
    }

    /// <summary>
    /// Spec 7.6: the client stops reconnecting for good. The first to close wins: after a disposal nothing happens.
    /// Otherwise the state is Closed with the failure kept, <see cref="Completion"/> fails with it, event 1106 is written
    /// once, the queued commands wake up to fail with it, and <see cref="Unsolicited"/> ends.
    /// </summary>
    /// <param name="ex">What the last attempt failed with.</param>
    /// <param name="attempts">How many attempts this loss got.</param>
    private void GiveUp(Exception ex, int attempts)
    {
        // Steps 1 and 2. A reentrant ConnectionRestored callback is named as the reason, with its own error as the cause.
        bool reentered = ex is FatalRestoreException;
        Exception cause = reentered ? ex.InnerException! : ex;
        var failure = new IOException(
            reentered
                ? $"Gave up reconnecting to {_target}: ConnectionRestored called the ResilientControlClient instead of context.Client."
                : $"Gave up reconnecting to {_target} after {attempts} attempt(s).",
            cause);

        // Step 3.
        TaskCompletionSource changed;
        lock (_sync)
        {
            if (_state == ClientState.Closed)
            {
                return;
            }

            _state = ClientState.Closed;
            _failure = failure;
            changed = SwapChanged();
        }

        // Step 4.
        changed.TrySetResult();
        if (_completion.TrySetException(failure))
        {
            // Reading Exception marks it observed, so a client nobody awaits does not raise an unobserved task exception.
            _ = _completion.Task.Exception;
        }

        try
        {
            ResilientClientLog.ReconnectGaveUp(
                _logger, _target, attempts,
                reentered ? "ConnectionRestored called the ResilientControlClient" : "attempts exhausted", failure);
        }
        catch (Exception)
        {
            // A logging provider failed; the supervisor never throws, and the client still closes.
        }
        finally
        {
            _lifetime.Cancel();
            _unsolicited.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Spec 7.4: one attempt of the reconnection pipeline. Keeps the 1 s floor since the previous attempt started,
    /// opens and verifies a connection, runs <see cref="ResilientControlClientOptions.ConnectionRestored"/> on it, and
    /// returns its link when the connection is still alive. On any failure the connection of this attempt is closed
    /// before the exception reaches the pipeline, which reads the phase it failed in from <paramref name="state"/>.
    /// </summary>
    private async ValueTask<Link> ReconnectOnceAsync(ResilienceContext context, ReconnectState state)
    {
        CancellationToken ct = context.CancellationToken;
        state.Attempt++;
        Link? link = null;
        try
        {
            // Step 1. Polly's jittered delay can be nearly zero, so the floor is kept here, at the start of every attempt.
            TimeSpan remaining = AttemptFloor - _time.GetElapsedTime(_lastAttemptStart, _time.GetTimestamp());
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time, ct).ConfigureAwait(false);
            }

            _lastAttemptStart = _time.GetTimestamp();

            // Steps 2-5, and step 9 for a failure inside them.
            link = await OpenLinkAsync(phase => state.Phase = phase, ct).ConfigureAwait(false);

            // Step 6.
            if (_options.ConnectionRestored is { } restore)
            {
                state.Phase = ReconnectPhase.Restore;
                await RestoreAsync(restore, link, state).ConfigureAwait(false);
            }

            // Step 7.
            if (!link.Client.IsConnected)
            {
                throw new IOException("The connection was lost while it was being restored.", LossCauseOf(link));
            }

            // Step 8.
            return link;
        }
        catch
        {
            // Step 9 for a failure after the link was opened.
            if (link is not null)
            {
                await CloseLinkAsync(link).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Spec 7.4 step 6 and 7.5: runs the <see cref="ResilientControlClientOptions.ConnectionRestored"/> callback on
    /// the verified connection through a <see cref="RestoreSession"/>, inside a <see cref="RestoreScope"/> that marks
    /// every command of this client made meanwhile as reentrant. The session ends and the scope closes however the
    /// callback finishes; a synchronous exception or a <see langword="null"/> task counts as a failed task.
    /// </summary>
    /// <exception cref="FatalRestoreException">The callback called this client; thrown even when the callback itself succeeded.</exception>
    private async Task RestoreAsync(
        Func<ConnectionRestoredContext, CancellationToken, Task> callback, Link link, ReconnectState state)
    {
        var scope = new RestoreScope(this) { Active = true };
        var session = new RestoreSession(this, link);
        long startedAt = _time.GetTimestamp();
        Exception? failure = null;
        _restoreScope.Value = scope;
        try
        {
            ResilientClientLog.RestoreStarted(_logger, link.Client.LocalEndPoint);
            Task restored;
            try
            {
                restored = callback(new ConnectionRestoredContext(session, state.Cause, state.LostAt), _lifetime.Token)
                    ?? Task.FromException(new InvalidOperationException("ConnectionRestored returned null instead of a task."));
            }
            catch (Exception ex)
            {
                restored = Task.FromException(ex);
            }

            await restored.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Kept until the scope is closed: a reentrant call outranks whatever the callback failed with.
            failure = ex;
        }
        finally
        {
            scope.Active = false;
            session.End();
            _restoreScope.Value = null;
        }

        if (scope.Reentered is { } reentered)
        {
            throw new FatalRestoreException(reentered);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        ResilientClientLog.RestoreCompleted(_logger, _time.GetElapsedTime(startedAt));
    }

    /// <summary>The state of one loss across the attempts of the reconnection pipeline (spec 8).</summary>
    private sealed class ReconnectState(Exception cause, DateTimeOffset lostAt, long lostTimestamp)
    {
        /// <summary>Why the connection was lost.</summary>
        public Exception Cause { get; } = cause;

        /// <summary>When it was lost, as <see cref="ConnectionRestoredContext"/> reports it.</summary>
        public DateTimeOffset LostAt { get; } = lostAt;

        /// <summary>The timestamp of the loss, for the downtime of event 1105.</summary>
        public long LostTimestamp { get; } = lostTimestamp;

        /// <summary>How many attempts this loss has had, the running one included.</summary>
        public int Attempt { get; set; }

        /// <summary>The phase the running attempt is in, for event 1104.</summary>
        public ReconnectPhase Phase { get; set; }
    }
}
