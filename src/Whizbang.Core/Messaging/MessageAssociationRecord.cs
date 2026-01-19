namespace Whizbang.Core.Messaging;

/// <summary>
/// Database entity for message_associations table.
/// Stores associations between message types and their consumers (perspectives, handlers, receptors).
/// Populated during startup via reconciliation to enable auto-creation of perspective checkpoints.
/// </summary>
/// <docs>core-concepts/message-associations</docs>
public sealed class MessageAssociationRecord {
  /// <summary>
  /// Unique identifier for this association.
  /// Primary key.
  /// </summary>
  public required Guid Id { get; set; }

  /// <summary>
  /// Fully-qualified message type name (e.g., "MyApp.Events.ProductCreated").
  /// </summary>
  public required string MessageType { get; set; }

  /// <summary>
  /// Type of association: "perspective", "receptor", or "handler".
  /// </summary>
  public required string AssociationType { get; set; }

  /// <summary>
  /// Name of the target consumer (perspective name, receptor name, or handler name).
  /// </summary>
  public required string TargetName { get; set; }

  /// <summary>
  /// Name of the service that owns this consumer.
  /// </summary>
  public required string ServiceName { get; set; }

  /// <summary>
  /// UTC timestamp when this association was created.
  /// </summary>
  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>
  /// UTC timestamp when this association was last updated.
  /// </summary>
  public DateTimeOffset UpdatedAt { get; set; }
}
