namespace NetSdr;

/// <summary>Thrown when the peer sends bytes that violate the NetSDR wire protocol.</summary>
public sealed class NetSdrProtocolException : NetSdrException
{
    public NetSdrProtocolException(string message)
        : base(message)
    {
    }

    public NetSdrProtocolException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
