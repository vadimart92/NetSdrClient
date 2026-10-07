using System.Net;
using Microsoft.Extensions.Logging;

namespace NetSdr.Data;

/// <summary>
/// The log events of <see cref="NetSdrDataReceiver"/>, ids 1200-1299. None of them is written per packet: a gap and
/// the first handler error of an interval are events, the rest is summed up per interval and at the end.
/// </summary>
internal static partial class DataReceiverLog
{
    [LoggerMessage(EventId = 1200, EventName = "ReceiveStarted", Level = LogLevel.Information,
        Message = "Receiving data on {LocalEndPoint}, receive buffer {ReceiveBufferBytes} bytes")]
    public static partial void ReceiveStarted(ILogger logger, IPEndPoint localEndPoint, int receiveBufferBytes);

    [LoggerMessage(EventId = 1201, EventName = "IntervalSummary", Level = LogLevel.Debug,
        Message = "{Received} packets, {Bytes} bytes in {Elapsed}; nothing lost or rejected")]
    public static partial void IntervalSummary(ILogger logger, long received, long bytes, TimeSpan elapsed);

    [LoggerMessage(EventId = 1202, EventName = "IntervalSummaryWithLoss", Level = LogLevel.Warning,
        Message = "{Received} packets, {Bytes} bytes in {Elapsed}; {Lost} lost, {Rejected} rejected, {HandlerErrors} handler errors")]
    public static partial void IntervalSummaryWithLoss(
        ILogger logger, long received, long bytes, TimeSpan elapsed, long lost, long rejected, long handlerErrors);

    [LoggerMessage(EventId = 1203, EventName = "ReceiveStopped", Level = LogLevel.Information,
        Message = "Stopped receiving on {LocalEndPoint} after {Elapsed}: {Received} packets, {Bytes} bytes, {Lost} lost, {Rejected} rejected, {HandlerErrors} handler errors")]
    public static partial void ReceiveStopped(
        ILogger logger,
        IPEndPoint? localEndPoint,
        TimeSpan elapsed,
        long received,
        long bytes,
        long lost,
        long rejected,
        long handlerErrors);

    [LoggerMessage(EventId = 1204, EventName = "SequenceGap", Level = LogLevel.Debug,
        Message = "{Gap} packets lost before sequence {Sequence}")]
    public static partial void SequenceGap(ILogger logger, int gap, ushort sequence);

    [LoggerMessage(EventId = 1205, EventName = "HandlerFailed", Level = LogLevel.Error,
        Message = "The data handler threw at sequence {Sequence}; later handler errors in this interval are only counted")]
    public static partial void HandlerFailed(ILogger logger, ushort sequence, Exception exception);

    [LoggerMessage(EventId = 1206, EventName = "ReceiveFailed", Level = LogLevel.Error,
        Message = "Receiving on {LocalEndPoint} stopped")]
    public static partial void ReceiveFailed(ILogger logger, IPEndPoint? localEndPoint, Exception exception);
}
