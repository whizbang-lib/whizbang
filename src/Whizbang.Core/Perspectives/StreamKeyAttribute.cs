namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marks a property as the stream key for perspective event processing.
/// The stream key identifies which aggregate/stream an event belongs to,
/// enabling perspectives to process events in order for each stream.
/// </summary>
/// <remarks>
/// Each event type used in a perspective MUST have exactly one property marked with [StreamKey].
/// The stream key is typically the aggregate ID (e.g., ProductId, OrderId, CustomerId).
/// </remarks>
/// <example>
/// <code>
/// public record ProductCreatedEvent : IEvent {
///     [StreamKey]
///     public Guid ProductId { get; init; }
///     public string ProductName { get; init; }
/// }
/// </code>
/// </example>
/// <docs>core-concepts/perspectives</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_EventWithStreamKey_ExtractsStreamKeyPropertyAsync</tests>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class StreamKeyAttribute : Attribute {
}
