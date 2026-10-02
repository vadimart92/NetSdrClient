using NetSdr.Identification;

namespace NetSdr.Examples.Vega;

/// <summary>
/// An error of the Vega client that is not a protocol error, such as connecting to a device that is not a Vega
/// receiver. It does not derive from <see cref="NetSdrException"/>: it belongs to the application level.
/// </summary>
public sealed class VegaException : Exception
{
    /// <param name="message">What went wrong.</param>
    /// <param name="identity">What was learned about the device, when the error concerns a device that was identified.</param>
    public VegaException(string message, DeviceIdentity? identity = null)
        : base(message)
    {
        Identity = identity;
    }

    /// <summary>What was learned about the device; <see langword="null"/> when the error is not about an identified device.</summary>
    public DeviceIdentity? Identity { get; }
}
