namespace Whizbang.Core;

/// <summary>
/// Marks a property or parameter as the stream identifier for event sourcing.
/// Used on both events and commands to identify which stream (aggregate) they belong to.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to a property or record parameter in your message types
/// to identify the stream (aggregate/entity) that the message is associated with.
/// </para>
/// <para>
/// Example with record parameter:
/// <code>
/// public record OrderCreated([property: StreamId] Guid OrderId, string ProductName) : IEvent;
/// </code>
/// </para>
/// <para>
/// Example with property:
/// <code>
/// public record CreateOrder : ICommand {
///   [StreamId]
///   public Guid OrderId { get; init; }
///   public string ProductName { get; init; }
/// }
/// </code>
/// </para>
/// <para>
/// The source generator will discover properties marked with [StreamId]
/// and generate compile-time extractor methods for zero-reflection stream ID resolution.
/// </para>
/// <para>
/// Requirements:
/// - Property must be of type <see cref="Guid"/>, <see cref="Guid"/>?, or a WhizbangId type
/// - Only one property per message type should have this attribute
/// - Attribute is inherited by derived message types
/// </para>
/// </remarks>
/// <docs>extending/attributes/streamid</docs>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class StreamIdAttribute : Attribute;
