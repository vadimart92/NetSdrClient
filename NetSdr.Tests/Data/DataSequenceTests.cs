using NetSdr.Data;

namespace NetSdr.Tests.Data;

public class DataSequenceTests
{
    [TestCase(0, 1)]
    [TestCase(0xFFFE, 0xFFFF)]
    [TestCase(0xFFFF, 1)]
    public void Next(int value, int expected) => Assert.That(DataSequence.Next((ushort)value), Is.EqualTo(expected));

    [TestCase(5, 5, 0)]
    [TestCase(5, 7, 2)]
    [TestCase(0xFFFF, 1, 1)]
    [TestCase(0xFFFE, 2, 3)]
    [TestCase(3, 1, 65533)]
    public void Distance(int expected, int actual, int distance) =>
        Assert.That(DataSequence.Distance((ushort)expected, (ushort)actual), Is.EqualTo(distance));
}
