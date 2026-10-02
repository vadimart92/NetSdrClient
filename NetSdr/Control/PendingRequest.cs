using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Control;

/// <summary>
/// The request in flight: the reply the reader loop is waiting for. Only the first call to
/// <see cref="Complete"/> or <see cref="Fail"/> has an effect.
/// </summary>
internal abstract class PendingRequest
{
    protected PendingRequest(ushort code, RequestType requestType)
    {
        Code = code;
        RequestType = requestType;
        ExpectedType = requestType == RequestType.GetRange ? ReplyType.RangeResponse : ReplyType.Response;
    }

    /// <summary>The item code the reply must carry.</summary>
    public ushort Code { get; }

    public RequestType RequestType { get; }

    /// <summary>The reply type the request waits for: <c>RangeResponse</c> for a range request, otherwise <c>Response</c>.</summary>
    public ReplyType ExpectedType { get; }

    /// <summary>
    /// Completes the request with the reply payload. <paramref name="payload"/> is only valid during the call.
    /// A payload that cannot be read fails the request with <see cref="NetSdrProtocolException"/>.
    /// </summary>
    public abstract void Complete(ReadOnlySpan<byte> payload);

    public abstract void Fail(Exception exception);
}

/// <summary>A request whose reply is read as the control item <typeparamref name="T"/>.</summary>
internal sealed class PendingRequest<T> : PendingRequest where T : struct, IControlItem<T>
{
    private readonly TaskCompletionSource<T> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingRequest(RequestType requestType)
        : base(T.Code, requestType)
    {
    }

    public Task<T> Reply => _reply.Task;

    public override void Complete(ReadOnlySpan<byte> payload)
    {
        T item;
        try
        {
            item = ControlItemMessage.ReadItem<T>(payload);
        }
        catch (NetSdrProtocolException ex)
        {
            _reply.TrySetException(ex);
            return;
        }

        _reply.TrySetResult(item);
    }

    public override void Fail(Exception exception) => _reply.TrySetException(exception);
}

/// <summary>A request whose reply is returned as an uninterpreted <see cref="ControlItemMessage"/>.</summary>
internal sealed class PendingRawRequest : PendingRequest
{
    private readonly TaskCompletionSource<ControlItemMessage> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingRawRequest(ushort code, RequestType requestType)
        : base(code, requestType)
    {
    }

    public Task<ControlItemMessage> Reply => _reply.Task;

    public override void Complete(ReadOnlySpan<byte> payload) =>
        _reply.TrySetResult(new ControlItemMessage(ExpectedType, Code, payload.ToArray()));

    public override void Fail(Exception exception) => _reply.TrySetException(exception);
}
