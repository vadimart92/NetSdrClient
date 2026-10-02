using NetSdr.Framing;

namespace NetSdr;

/// <summary>Thrown when the device answers a request with a NAK (a reply of length 2).</summary>
public sealed class NetSdrNakException : NetSdrException
{
    public NetSdrNakException(ushort code, RequestType requestType)
        : base($"Device rejected {requestType} of control item 0x{code:X4} (NAK).")
    {
        Code = code;
        RequestType = requestType;
    }

    /// <summary>Control item code of the rejected request.</summary>
    public ushort Code { get; }

    /// <summary>Type of the rejected request.</summary>
    public RequestType RequestType { get; }
}
