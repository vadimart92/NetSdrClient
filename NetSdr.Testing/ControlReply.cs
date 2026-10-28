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
        Builtin,
        BuiltinPayload,
    }

    private readonly ReplyKind _kind;
    private readonly ushort? _code;
    private readonly ReadOnlyMemory<byte> _payload;
    private readonly TimeSpan _delay;
    private readonly ControlRequest[]? _then;

    private ControlReply(
        ReplyKind kind,
        ushort? code = null,
        ReadOnlyMemory<byte> payload = default,
        TimeSpan delay = default,
        ControlRequest[]? then = null)
    {
        _kind = kind;
        _code = code;
        _payload = payload;
        _delay = delay;
        _then = then;
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

    /// <summary>
    /// Runs the request through the server's built-in behaviour, as if no handler were set: <see cref="NetSdrTestServer.AutoStream"/>
    /// and the stored state, so a Set is stored and echoed and a Get is answered from the state. A handler that only
    /// watches requests, or changes them for some types only, returns this for the rest.
    /// </summary>
    public static ControlReply Builtin() => new(ReplyKind.Builtin);

    /// <summary>
    /// Runs the request through the server's built-in behaviour, see <see cref="Builtin()"/>, with <paramref name="item"/>
    /// in place of the payload the client sent: a Set stores and echoes <paramref name="item"/>, and the stream of a Run
    /// follows it. <see cref="NetSdrTestServer.Received"/> keeps what the client sent. An item whose code is not the code
    /// of the request turns the reply into a NAK and is recorded in <see cref="NetSdrTestServer.HandlerErrors"/>.
    /// </summary>
    public static ControlReply Builtin<T>(T item) where T : struct, IControlItem<T>
    {
        var payload = new byte[T.GetSize(in item)];
        T.Write(in item, payload);
        return new ControlReply(ReplyKind.BuiltinPayload, T.Code, payload);
    }

    /// <summary>
    /// Runs the request through the server's built-in behaviour, see <see cref="Builtin()"/>, with <paramref name="payload"/>
    /// in place of the bytes after the item code: the item of a Set, the key of a Get.
    /// </summary>
    public static ControlReply Builtin(ReadOnlyMemory<byte> payload) => new(ReplyKind.BuiltinPayload, payload: payload.ToArray());

    /// <summary>Returns this reply, sent only after <paramref name="delay"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative or longer than <see cref="int.MaxValue"/> milliseconds.</exception>
    public ControlReply After(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay.TotalMilliseconds, int.MaxValue);
        return new ControlReply(_kind, _code, _payload, delay, _then);
    }

    /// <summary>
    /// Returns this reply, with <paramref name="commands"/> run by the server as if the client had sent them: one after
    /// another, through the handlers, <see cref="NetSdrTestServer.AutoStream"/> and the stored state, before this reply
    /// is written. What a command does after its own reply, such as starting the stream of a Run, happens once this
    /// reply has been written. So a vendor item can start the stream that the standard receiver state starts. The
    /// replies to the commands are not sent and the commands are not added to <see cref="NetSdrTestServer.Received"/>.
    /// A command that fails turns this reply into a NAK and is recorded in <see cref="NetSdrTestServer.HandlerErrors"/>;
    /// a command answered with a NAK on purpose does not. A reply of a command may chain commands too, up to 8 levels deep.
    /// Calling this again adds to the commands.
    /// </summary>
    public ControlReply Then(params ControlRequest[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return new ControlReply(_kind, _code, _payload, _delay, [.. _then ?? [], .. commands]);
    }

    internal bool IsSilent => _kind == ReplyKind.Silent;

    /// <summary>Whether the server answers with its built-in behaviour, see <see cref="Builtin()"/>.</summary>
    internal bool IsBuiltin => _kind is ReplyKind.Builtin or ReplyKind.BuiltinPayload;

    /// <summary>The request the built-in behaviour runs for a <see cref="Builtin()"/> reply to <paramref name="request"/>.</summary>
    /// <exception cref="InvalidOperationException">The item of <see cref="Builtin{T}"/> has another code than the request.</exception>
    internal ControlRequest BuiltinRequest(in ControlRequest request)
    {
        if (_kind != ReplyKind.BuiltinPayload)
        {
            return request;
        }

        if (_code is { } code && code != request.Code)
        {
            throw new InvalidOperationException(
                $"ControlReply.Builtin was given an item with code 0x{code:X4} for a request with code 0x{request.Code:X4}.");
        }

        return new ControlRequest(request.Type, request.Code, _payload);
    }

    /// <summary>
    /// The reply to send for a <see cref="Builtin()"/> reply: what the built-in behaviour answered to <paramref name="builtin"/>,
    /// an echo of its payload rather than the client's, with the delay and commands of this reply.
    /// </summary>
    internal ControlReply WithBuiltinAnswer(ControlReply answer, in ControlRequest builtin) => answer._kind == ReplyKind.Echo
        ? new ControlReply(ReplyKind.Payload, builtin.Code, builtin.Payload, _delay, _then)
        : new ControlReply(answer._kind, answer._code, answer._payload, _delay, _then);

    internal TimeSpan Delay => _delay;

    /// <summary>The commands the server runs before this reply is written; empty when there are none.</summary>
    internal IReadOnlyList<ControlRequest> Commands => _then ?? [];

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
