using System.Buffers;
using System.IO.Pipelines;
using NetSdr.Control;
using NetSdr.Framing;

namespace NetSdr.Tests.Control;

/// <summary>
/// An in-memory stand-in for the device: a client attached to a pair of pipes, with helpers to read the
/// frames the client sends and to feed it reply bytes.
/// </summary>
internal sealed class PipeDevice : IAsyncDisposable
{
    private readonly Pipe _toClient;
    private readonly Pipe _fromClient;

    private PipeDevice(NetSdrControlClient client, Pipe toClient, Pipe fromClient)
    {
        Client = client;
        _toClient = toClient;
        _fromClient = fromClient;
    }

    public NetSdrControlClient Client { get; }

    /// <param name="options">Client options.</param>
    /// <param name="toClient">Options of the pipe that carries device-to-client bytes.</param>
    public static PipeDevice Create(NetSdrControlClientOptions? options = null, PipeOptions? toClient = null)
    {
        var toClientPipe = new Pipe(toClient ?? new PipeOptions(useSynchronizationContext: false));
        var fromClientPipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var client = new NetSdrControlClient(options);
        client.Attach(toClientPipe.Reader, fromClientPipe.Writer.AsStream());
        return new PipeDevice(client, toClientPipe, fromClientPipe);
    }

    /// <summary>Reads exactly one frame the client sent, waiting at most <see cref="Limits.Test"/>.</summary>
    public async Task<byte[]> ReadRequestAsync()
    {
        using var timeout = new CancellationTokenSource(Limits.Test);
        PipeReader reader = _fromClient.Reader;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(timeout.Token);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (buffer.Length >= FrameHeader.Size)
                {
                    byte[] header = buffer.Slice(0, FrameHeader.Size).ToArray();
                    if (!FrameHeader.TryRead(header, out int length, out _))
                    {
                        throw new InvalidOperationException("The client sent an invalid frame header.");
                    }

                    if (buffer.Length >= length)
                    {
                        byte[] frame = buffer.Slice(0, length).ToArray();
                        reader.AdvanceTo(buffer.GetPosition(length));
                        return frame;
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    throw new EndOfStreamException("The client closed its stream before sending a whole frame.");
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The client sent no whole frame within {Limits.Test}.");
        }
    }

    /// <summary>Delivers the bytes to the client in a single write.</summary>
    public async Task SendAsync(string hex)
    {
        await _toClient.Writer.WriteAsync(Hex.Parse(hex));
    }

    /// <summary>Delivers the bytes one at a time, flushing after each, so the client sees partial frames.</summary>
    public async Task SendBytewiseAsync(string hex)
    {
        foreach (byte value in Hex.Parse(hex))
        {
            await _toClient.Writer.WriteAsync(new[] { value });
            await Task.Delay(1);
        }
    }

    /// <summary>Ends the device-to-client stream, as a device closing the connection would.</summary>
    public void CloseRemote() => _toClient.Writer.Complete();

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _toClient.Writer.Complete();
        _fromClient.Reader.Complete();
    }
}
