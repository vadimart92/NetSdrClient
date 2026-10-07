using System.Net;

namespace NetSdr.Control;

/// <summary>What <see cref="IDeviceRebooter.RebootAsync"/> gets to find the device it reboots.</summary>
public sealed class RebootContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="target">What <see cref="ResilientControlClient"/> connects to: "host:port" or the end point.</param>
    /// <param name="lastRemoteEndPoint">The device end of the most recent connection, null if none.</param>
    /// <param name="requested">True for <see cref="ResilientControlClient.RebootAsync"/>, false for an escalation of the policy.</param>
    public RebootContext(string target, IPEndPoint? lastRemoteEndPoint, bool requested)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        LastRemoteEndPoint = lastRemoteEndPoint;
        Requested = requested;
    }

    /// <summary>What <see cref="ResilientControlClient"/> connects to: "host:port" or the end point.</summary>
    public string Target { get; }

    /// <summary>The device end of the most recent connection, null if none.</summary>
    public IPEndPoint? LastRemoteEndPoint { get; }

    /// <summary>True for <see cref="ResilientControlClient.RebootAsync"/>, false for an escalation of the policy.</summary>
    public bool Requested { get; }
}
