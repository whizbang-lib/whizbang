namespace Whizbang.Core;

/// <summary>
/// Marks a property as the stream key for event sourcing.
/// The stream key identifies which stream (aggregate) an event belongs to.
/// Used by the source generator to create compile-time stream key resolvers.
/// </summary>
/// <docs>attributes/streamkey</docs>
/// <tests>tests/Whizbang.Core.Tests/StreamKeyAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class StreamKeyAttribute : Attribute {
}
