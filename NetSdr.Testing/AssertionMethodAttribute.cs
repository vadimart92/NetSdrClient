namespace NetSdr.Testing;

/// <summary>
/// Marks a method that fails the test when its condition does not hold, so analyzers (Sonar S2699, ReSharper) count a
/// call to it as an assertion.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AssertionMethodAttribute : Attribute;
