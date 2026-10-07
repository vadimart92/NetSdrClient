namespace NetSdr.Control;

/// <summary>The supervisor's side of the client: the end of its life (spec 7.7). Loss, reconnection and the heartbeat join here.</summary>
public sealed partial class ResilientControlClient
{
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Closes the connection and ends the client. Requests in flight, queued or made later fail with
    /// <see cref="ObjectDisposedException"/>; <see cref="Completion"/> completes successfully, unless the client had
    /// already given up, and then <see cref="Unsolicited"/> ends. Idempotent, and never throws.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource changed;
        Link link;
        lock (_sync)
        {
            if (_disposed)
            {
                return new ValueTask(_disposal.Task);
            }

            _disposed = true;
            _state = ClientState.Closed;
            changed = SwapChanged();
            link = _link;
        }

        changed.TrySetResult();
        return new ValueTask(DisposeCoreAsync(link));
    }

    private async Task DisposeCoreAsync(Link link)
    {
        try
        {
            // Stops every wait on the lifetime token: queued commands and waits for a connection end with ObjectDisposedException.
            _lifetime.Cancel();
            await link.Client.DisposeAsync().ConfigureAwait(false);
            await link.Pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The inner client's disposal and the pump never throw; nothing else is left to clean up.
        }
        finally
        {
            // Completion first, so whoever sees Unsolicited end finds its outcome already there. A give-up failure is kept.
            _completion.TrySetResult();
            try
            {
                ResilientClientLog.Disposed(_logger, _target);
            }
            catch (Exception)
            {
                // A logging provider failed; the disposal still completes.
            }
            finally
            {
                _unsolicited.Writer.TryComplete();
                _disposal.TrySetResult();
            }
        }
    }
}
