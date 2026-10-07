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

    /// <summary>Started by <c>ConnectAsync</c> once the first connection is published; never faults, and <see cref="DisposeAsync"/> waits for it.</summary>
    private Task _supervisor = Task.CompletedTask;

    /// <summary>
    /// Closes the connection and ends the client. Requests in flight, queued or made later fail with
    /// <see cref="ObjectDisposedException"/>; <see cref="Completion"/> completes successfully, unless the client had
    /// already given up, and then <see cref="Unsolicited"/> ends. Idempotent, and never throws.
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
        return new ValueTask(DisposeCoreAsync(link, restoring));
    }

    /// <param name="link">The published connection.</param>
    /// <param name="restoring">The connection of the reconnection attempt in progress, if one was.</param>
    private async Task DisposeCoreAsync(Link link, Link? restoring)
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
            await _supervisor.ConfigureAwait(false);
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
    /// Returns when the inner client of <paramref name="link"/> has ended, for whatever reason, without throwing: the
    /// supervisor reads the cause from the client itself. The heartbeat (spec 7.3) joins this wait.
    /// </summary>
    private async Task WatchAsync(Link link)
    {
        try
        {
            await link.Client.Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The fault is the loss; SuperviseAsync takes it from Completion.
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
        // Step 1. A fatal restore failure unwraps to the reentrancy error here, with the ConnectionRestored callback.
        Exception cause = ex;
        var failure = new IOException($"Gave up reconnecting to {_target} after {attempts} attempt(s).", cause);

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
            ResilientClientLog.ReconnectGaveUp(_logger, _target, attempts, "attempts exhausted", failure);
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
    /// opens and verifies a connection, and returns its link when the connection is still alive. On any failure the
    /// connection of this attempt is closed before the exception reaches the pipeline, which reads the phase it failed
    /// in from <paramref name="state"/>.
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

            // Step 6, the ConnectionRestored callback, runs here.

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
