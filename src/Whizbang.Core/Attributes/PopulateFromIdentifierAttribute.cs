namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a message property for automatic population from message identifiers.
/// The property will be set with values from the MessageEnvelope (MessageId, CorrelationId, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to Guid or Guid? properties on message types (commands, events)
/// to automatically capture message identifiers for correlation and causation tracking.
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
/// public record ShipmentDispatched(
///   [property: StreamId] Guid ShipmentId,
///   string TrackingNumber,
///   [property: PopulateFromIdentifier(IdentifierKind.CorrelationId)] Guid? WorkflowId = null,
///   [property: PopulateFromIdentifier(IdentifierKind.CausationId)] Guid? TriggeredBy = null
/// ) : IEvent;
/// </code>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateFromIdentifierAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class PopulateFromIdentifierAttribute(IdentifierKind kind) : Attribute {
  /// <summary>
  /// Gets the kind of identifier to populate.
  /// </summary>
  public IdentifierKind Kind { get; } = kind;
}
