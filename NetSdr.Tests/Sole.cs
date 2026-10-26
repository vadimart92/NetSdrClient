namespace NetSdr.Tests;

internal static class SoleExtensions
{
    /// <summary>Asserts that <paramref name="items"/> holds exactly one item and returns it.</summary>
    public static T Sole<T>(this IEnumerable<T> items)
    {
        List<T> list = [.. items];
        Assert.That(list, Has.Exactly(1).Items);
        return list[0];
    }
}
