using System.Net;
using Microsoft.Extensions.Logging;
using NetSdr.Framing;

namespace NetSdr.Control;

/// <summary>Why a frame went to <see cref="NetSdrControlClient.Unsolicited"/>, as event 1009 reports it.</summary>
internal enum PublishReason
{
    /// <summary>A frame of type <c>Unsolicited</c>.</summary>
    Unsolicited,

    /// <summary>A data item or an acknowledgement.</summary>
    Data,

    /// <summary>A response that freed the slot of an abandoned request.</summary>
    LateReply,

    /// <summary>A response nobody waits for.</summary>
    NoRequest,

    /// <summary>A frame that failed the request in flight.</summary>
    Foreign,

    /// <summary>A NAK with no request in flight.</summary>
    Nak,
}

/// <summary>
/// The log events of <see cref="NetSdrControlClient"/>, ids 1000-1099. Events whose level depends on
/// <see cref="NetSdrControlClientOptions.Supervised"/> take the level as a parameter.
/// </summary>
internal static partial class ControlClientLog
{
    [LoggerMessage(EventId = 1000, EventName = "Connected", Message = "Connected to {RemoteEndPoint} from {LocalEndPoint}")]
    public static partial void Connected(
        ILogger logger, LogLevel level, IPEndPoint? remoteEndPoint, IPEndPoint? localEndPoint);

    [LoggerMessage(EventId = 1001, EventName = "Closed", Message = "Connection to {RemoteEndPoint} closed")]
    public static partial void Closed(ILogger logger, LogLevel level, IPEndPoint? remoteEndPoint);

    [LoggerMessage(EventId = 1002, EventName = "Faulted", Message = "Connection to {RemoteEndPoint} failed")]
    public static partial void Faulted(ILogger logger, LogLevel level, IPEndPoint? remoteEndPoint, Exception exception);

    [LoggerMessage(EventId = 1003, EventName = "RequestSent", Level = LogLevel.Debug,
        Message = "{RequestType} {Item} 0x{Code:X4} sent, {PayloadLength} bytes")]
    public static partial void RequestSent(
        ILogger logger, RequestType requestType, string item, ushort code, int payloadLength);

    [LoggerMessage(EventId = 1004, EventName = "ReplyReceived", Level = LogLevel.Debug,
        Message = "{ReplyType} {Item} 0x{Code:X4} received after {Duration}, {PayloadLength} bytes")]
    public static partial void ReplyReceived(
        ILogger logger, ReplyType replyType, string item, ushort code, TimeSpan duration, int payloadLength);

    [LoggerMessage(EventId = 1005, EventName = "NakReceived", Level = LogLevel.Debug,
        Message = "NAK for {RequestType} {Item} 0x{Code:X4} after {Duration}")]
    public static partial void NakReceived(
        ILogger logger, RequestType requestType, string item, ushort code, TimeSpan duration);

    [LoggerMessage(EventId = 1006, EventName = "RequestTimedOut",
        Message = "No reply to {RequestType} {Item} 0x{Code:X4} within {Timeout}; faulting the client: {Faults}")]
    public static partial void RequestTimedOut(
        ILogger logger, LogLevel level, RequestType requestType, string item, ushort code, TimeSpan timeout, bool faults);

    [LoggerMessage(EventId = 1007, EventName = "ForeignReply", Level = LogLevel.Warning,
        Message = "Expected {ExpectedType} 0x{Code:X4} but received {ReplyType} 0x{ReceivedCode:X4}; the request failed")]
    public static partial void ForeignReply(
        ILogger logger, ReplyType expectedType, ushort code, ReplyType replyType, ushort receivedCode);

    [LoggerMessage(EventId = 1008, EventName = "RequestAbandoned", Level = LogLevel.Debug,
        Message = "{RequestType} {Item} 0x{Code:X4} cancelled after it was sent; a late reply goes to Unsolicited")]
    public static partial void RequestAbandoned(ILogger logger, RequestType requestType, string item, ushort code);

    [LoggerMessage(EventId = 1009, EventName = "MessagePublished", Level = LogLevel.Debug,
        Message = "{ReplyType} 0x{Code:X4}, {PayloadLength} bytes, to Unsolicited ({Reason})")]
    public static partial void MessagePublished(
        ILogger logger, ReplyType replyType, ushort code, int payloadLength, PublishReason reason);

    [LoggerMessage(EventId = 1010, EventName = "FrameSent", Level = LogLevel.Trace, Message = "-> {Hex}")]
    public static partial void FrameSent(ILogger logger, string hex);

    [LoggerMessage(EventId = 1011, EventName = "FrameReceived", Level = LogLevel.Trace, Message = "<- {Hex}")]
    public static partial void FrameReceived(ILogger logger, string hex);
}
