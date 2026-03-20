#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a message property for automatic timestamp population during message lifecycle.
/// The property will be set with a DateTimeOffset at the specified lifecycle moment.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to DateTimeOffset or DateTimeOffset? properties on message types
/// (commands, events) to automatically capture timing information without manual code.
/// </para>
/// <para>
/// Values are stored in the MessageEnvelope metadata to preserve message immutability.
/// Access populated values via envelope extension methods or use Materialize&lt;T&gt;()
/// to create a new message instance with populated values.
/// </para>
/// <para>
/// <strong>Example:</strong>
/// </para>
/// <code>
/// public record OrderCreated(
///   [property: StreamId] Guid OrderId,
///   [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null,
///   [property: PopulateTimestamp(TimestampKind.QueuedAt)] DateTimeOffset? QueuedAt = null
/// ) : IEvent;
/// </code>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateTimestampAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class PopulateTimestampAttribute(TimestampKind kind) : Attribute {
  /// <summary>
  /// Gets the kind of timestamp to populate.
  /// </summary>
  public TimestampKind Kind { get; } = kind;
}
