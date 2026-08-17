namespace Whizbang.Core.Attributes;

/// <summary>
/// States that a perspective's exclusion from stream deletion groups is DELIBERATE: it shares
/// streams with grouped siblings but keeps its rows on its own retention. Silences the group-drift
/// analyzer (WHIZ140) so "I chose to keep these streams" is distinguishable from "I forgot to join".
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Generators.Tests/StreamGroupAnalyzerTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StreamGroupIsolatedAttribute : Attribute;
