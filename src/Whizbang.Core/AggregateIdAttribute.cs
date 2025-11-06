using System;

namespace Whizbang.Core;

/// <summary>
/// Marks a property as the aggregate ID for a message.
/// Used by source generators to enable zero-reflection aggregate ID extraction
/// in PolicyContext and routing scenarios.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to a Guid property in your message types to identify
/// the aggregate (entity) that the message is associated with.
/// </para>
/// <para>
/// Example:
/// <code>
/// public record CreateOrder {
///   [AggregateId]
///   public Guid OrderId { get; init; }
///
///   public string ProductName { get; init; }
///   public decimal Amount { get; init; }
/// }
/// </code>
/// </para>
/// <para>
/// The source generator will discover properties marked with [AggregateId]
/// and generate compile-time extractor methods that PolicyContext.GetAggregateId()
/// uses to extract the ID without reflection.
/// </para>
/// <para>
/// Requirements:
/// - Property must be of type <see cref="Guid"/> or <see cref="Guid"/>?
/// - Only one property per message type should have this attribute
/// - Attribute is inherited by derived message types
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AggregateIdAttribute : Attribute {
}
