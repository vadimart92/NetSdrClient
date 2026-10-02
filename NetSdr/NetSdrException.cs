namespace NetSdr;

/// <summary>Base class for all exceptions specific to the NetSdr framework.</summary>
public class NetSdrException : Exception
{
    public NetSdrException(string message)
        : base(message)
    {
    }

    public NetSdrException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
