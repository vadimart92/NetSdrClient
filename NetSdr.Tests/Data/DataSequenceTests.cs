using NetSdr.Data;

namespace NetSdr.Tests.Data;

public class DataSequenceTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0xFFFE, 0xFFFF)]
    [InlineData(0xFFFF, 1)]
    public void Next(int value, int expected) => Assert.Equal(expected, DataSequence.Next((ushort)value));

    [Theory]
    [InlineData(5, 5, 0)]
    [InlineData(5, 7, 2)]
    [InlineData(0xFFFF, 1, 1)]
    [InlineData(0xFFFE, 2, 3)]
    [InlineData(3, 1, 65533)]
    public void Distance(int expected, int actual, int distance) =>
        Assert.Equal(distance, DataSequence.Distance((ushort)expected, (ushort)actual));
}
