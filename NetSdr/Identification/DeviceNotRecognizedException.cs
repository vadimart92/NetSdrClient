namespace NetSdr.Identification;

/// <summary>
/// Thrown when no registration of a <see cref="DeviceCatalog{TDevice}"/> matches the device and the catalog has no
/// default.
/// </summary>
public sealed class DeviceNotRecognizedException : NetSdrException
{
    public DeviceNotRecognizedException(DeviceIdentity identity, IReadOnlyList<string> candidates)
        : base(BuildMessage(identity, candidates))
    {
        Identity = identity;
        Candidates = candidates.ToArray();
    }

    /// <summary>What was learned about the device.</summary>
    public DeviceIdentity Identity { get; }

    /// <summary>The names of the registrations that were tried, in the order they were registered.</summary>
    public IReadOnlyList<string> Candidates { get; }

    private static string BuildMessage(DeviceIdentity identity, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(candidates);

        return $"Device was not recognized (name: {identity.Name ?? "?"}, product id: {identity.ProductId?.ToString("X8") ?? "?"}); candidates: {string.Join(", ", candidates)}.";
    }
}
