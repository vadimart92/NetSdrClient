using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Testing;

/// <summary>
/// What the test server answers to a control request. <c>default</c> is <see cref="Echo"/>.
/// </summary>
public readonly struct ControlReply
{
    private static readonly byte[] NakFrame = BuildNakFrame();

    private enum ReplyKind : byte
    {
        Echo,
        Nak,
        Silent,
        Payload,
    }

    private readonly ReplyKind _kind;
    private readonly ushort? _code;
    private readonly ReadOnlyMemory<byte> _payload;
    private readonly TimeSpan _delay;

    private ControlReply(ReplyKind kind, ushort? code = null, ReadOnlyMemory<byte> payload = default, TimeSpan delay = default)
    {
        _kind = kind;
        _code = code;
        _payload = payload;
        _delay = delay;
    }

    /// <summary>Answers with the request's item code and payload.</summary>
    public static ControlReply Echo => new(ReplyKind.Echo);

    /// <summary>Rejects the request with a header-only frame.</summary>
    public static ControlReply Nak => new(ReplyKind.Nak);

    /// <summary>Sends nothing, so the client's request times out.</summary>
    public static ControlReply Silent => new(ReplyKind.Silent);

    /// <summary>Answers with the request's item code and <paramref name="payload"/>.</summary>
    public static ControlReply Bytes(ReadOnlyMemory<byte> payload) => new(ReplyKind.Payload, payload: payload.ToArray());

    /// <summary>Answers with the code of <typeparamref name="T"/> and <paramref name="item"/> as the payload.</summary>
    public static ControlReply Item<T>(T item) where T : struct, IControlItem<T>
    {
        var payload = new byte[T.GetSize(in item)];
        T.Write(in item, payload);
        return new ControlReply(ReplyKind.Payload, T.Code, payload);
    }

    /// <summary>Returns this reply, sent only after <paramref name="delay"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative or longer than <see cref="int.MaxValue"/> milliseconds.</exception>
    public ControlReply After(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay.TotalMilliseconds, int.MaxValue);
        return new ControlReply(_kind, _code, _payload, delay);
    }

    internal bool IsSilent => _kind == ReplyKind.Silent;

    internal TimeSpan Delay => _delay;

    /// <summary>The frame to send for <paramref name="request"/>. Not valid for a silent reply.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The reply does not fit in one frame.</exception>
    internal ReadOnlyMemory<byte> ToFrame(in ControlRequest request)
    {
        if (_kind == ReplyKind.Nak)
        {
            return NakFrame;
        }

        ReplyType type = request.Type == RequestType.GetRange ? ReplyType.RangeResponse : ReplyType.Response;
        return _kind == ReplyKind.Echo
            ? ControlFrames.Encode((byte)type, request.Code, request.Payload.Span)
            : ControlFrames.Encode((byte)type, _code ?? request.Code, _payload.Span);
    }

    /// <summary>The header-only frame that rejects a request.</summary>
    internal static ReadOnlyMemory<byte> NakFrameBytes => NakFrame;

    private static byte[] BuildNakFrame()
    {
        var frame = new byte[FrameHeader.Size];
        FrameHeader.Write(frame, frame.Length, (byte)ReplyType.Response);
        return frame;
    }
}
