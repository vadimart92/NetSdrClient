namespace NetSdr.Tests;

internal static class Hex
{
    public static byte[] Parse(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));
}
