using System.Net;
using Microsoft.Extensions.Logging;
using NetSdr.Framing;

namespace NetSdr.Control;

/// <summary>What a late reply was, as events 1107 and 1108 report it.</summary>
internal enum LateOutcome
{
    Reply,
    Nak,
}

/// <summary>Who stopped waiting for the request a late reply answers, as event 1108 reports it.</summary>
internal enum LateOwner
{
    Heartbeat,
    CancelledCaller,
}

/// <summary>The log events of <see cref="ResilientControlClient"/>, ids 1100-1199.</summary>
internal static partial class ResilientClientLog
{
    [LoggerMessage(EventId = 1100, EventName = "CommandRetrying", Level = LogLevel.Warning,
        Message = "{RequestType} {Item} 0x{Code:X4} attempt {Attempt} failed ({Reason}); retrying")]
    public static partial void CommandRetrying(
        ILogger logger, RequestType requestType, string item, ushort code, int attempt, string reason, Exception exception);

    [LoggerMessage(EventId = 1101, EventName = "HeartbeatMissed", Level = LogLevel.Warning,
        Message = "No reply to the heartbeat within {ResponseTimeout}; waiting up to {LateReplyTimeout} for it")]
    public static partial void HeartbeatMissed(ILogger logger, TimeSpan responseTimeout, TimeSpan lateReplyTimeout);

    [LoggerMessage(EventId = 1102, EventName = "ConnectionUnresponsive", Level = LogLevel.Warning,
        Message = "{RequestType} 0x{Code:X4} unanswered for {Elapsed}; closing the connection to {RemoteEndPoint}")]
    public static partial void ConnectionUnresponsive(
        ILogger logger, RequestType requestType, ushort code, TimeSpan elapsed, IPEndPoint? remoteEndPoint);

    [LoggerMessage(EventId = 1103, EventName = "ConnectionLost", Level = LogLevel.Warning,
        Message = "Connection to {RemoteEndPoint} lost; reconnecting")]
    public static partial void ConnectionLost(ILogger logger, IPEndPoint? remoteEndPoint, Exception cause);

    [LoggerMessage(EventId = 1104, EventName = "ReconnectAttemptFailed", Level = LogLevel.Warning,
        Message = "Reconnect attempt {Attempt} to {Target} failed in phase {Phase}; next attempt in {Delay}")]
    public static partial void ReconnectAttemptFailed(
        ILogger logger, int attempt, string target, ReconnectPhase phase, TimeSpan delay, Exception exception);

    [LoggerMessage(EventId = 1105, EventName = "Reconnected", Level = LogLevel.Information,
        Message = "Reconnected to {RemoteEndPoint} from {LocalEndPoint} after {Attempts} attempt(s), {Downtime} without a connection")]
    public static partial void Reconnected(
        ILogger logger, IPEndPoint? remoteEndPoint, IPEndPoint? localEndPoint, int attempts, TimeSpan downtime);

    [LoggerMessage(EventId = 1106, EventName = "ReconnectGaveUp", Level = LogLevel.Error,
        Message = "Gave up reconnecting to {Target} after {Attempts} attempt(s): {Reason}")]
    public static partial void ReconnectGaveUp(ILogger logger, string target, int attempts, string reason, Exception exception);

    [LoggerMessage(EventId = 1107, EventName = "LateReplyAdopted", Level = LogLevel.Debug,
        Message = "{RequestType} {Item} 0x{Code:X4} answered ({Outcome}) {Late} after its response timeout")]
    public static partial void LateReplyAdopted(
        ILogger logger, RequestType requestType, string item, ushort code, LateOutcome outcome, TimeSpan late);

    [LoggerMessage(EventId = 1108, EventName = "LateReplyDrained", Level = LogLevel.Debug,
        Message = "Late {Outcome} for {RequestType} 0x{Code:X4} that nobody waits for ({Owner})")]
    public static partial void LateReplyDrained(
        ILogger logger, LateOutcome outcome, RequestType requestType, ushort code, LateOwner owner);

    [LoggerMessage(EventId = 1109, EventName = "Connected", Level = LogLevel.Information,
        Message = "Connected to {RemoteEndPoint} from {LocalEndPoint}")]
    public static partial void Connected(ILogger logger, IPEndPoint? remoteEndPoint, IPEndPoint? localEndPoint);

    [LoggerMessage(EventId = 1110, EventName = "RestoreStarted", Level = LogLevel.Debug,
        Message = "Running ConnectionRestored on {LocalEndPoint}")]
    public static partial void RestoreStarted(ILogger logger, IPEndPoint? localEndPoint);

    [LoggerMessage(EventId = 1111, EventName = "RestoreCompleted", Level = LogLevel.Debug,
        Message = "ConnectionRestored finished in {Duration}")]
    public static partial void RestoreCompleted(ILogger logger, TimeSpan duration);

    [LoggerMessage(EventId = 1112, EventName = "Disposed", Level = LogLevel.Information,
        Message = "Client for {Target} disposed")]
    public static partial void Disposed(ILogger logger, string target);

    [LoggerMessage(EventId = 1113, EventName = "RebootRequested", Level = LogLevel.Information,
        Message = "A {Kind} reboot of {Target} was requested")]
    public static partial void RebootRequested(ILogger logger, RebootKind kind, string target);

    [LoggerMessage(EventId = 1114, EventName = "RebootEscalated", Level = LogLevel.Warning,
        Message = "Escalating to a {Kind} reboot of {Target} after {FailedAttempts} failed attempt(s), the last in phase {Phase}")]
    public static partial void RebootEscalated(
        ILogger logger, RebootKind kind, string target, int failedAttempts, ReconnectPhase phase, Exception exception);

    [LoggerMessage(EventId = 1115, EventName = "RebootFailed", Level = LogLevel.Warning,
        Message = "The {Kind} reboot of {Target} failed")]
    public static partial void RebootFailed(ILogger logger, RebootKind kind, string target, Exception exception);

    [LoggerMessage(EventId = 1116, EventName = "RebootAccepted", Level = LogLevel.Information,
        Message = "{Target} accepted a {Kind} reboot; waiting {BootTime} for it to boot")]
    public static partial void RebootAccepted(ILogger logger, string target, RebootKind kind, TimeSpan bootTime);

    [LoggerMessage(EventId = 1117, EventName = "RecoveryPolicyFailed", Level = LogLevel.Warning,
        Message = "The recovery policy failed; continuing with the next attempt")]
    public static partial void RecoveryPolicyFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1118, EventName = "ConnectAttemptFailed", Level = LogLevel.Warning,
        Message = "Connect attempt {Attempt} of {Attempts} to {Target} failed in phase {Phase}; next attempt in {Delay}")]
    public static partial void ConnectAttemptFailed(
        ILogger logger, int attempt, int attempts, string target, ReconnectPhase phase, TimeSpan delay, Exception exception);
}
