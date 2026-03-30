namespace Whizbang.Core.Attributes;

/// <summary>
/// Specifies the kind of timestamp to auto-populate on a message property.
/// Each kind corresponds to a specific point in the message lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Use with <see cref="PopulateTimestampAttribute"/> to automatically capture
/// timestamps at different stages of message processing.
/// </para>
/// <para>
/// <list type="bullet">
/// <item><description><see cref="SentAt"/> - When dispatcher.SendAsync/PublishAsync is called</description></item>
/// <item><description><see cref="QueuedAt"/> - After message is persisted to outbox</description></item>
/// <item><description><see cref="DeliveredAt"/> - When message arrives from transport</description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateTimestampAttributeTests.cs</tests>
public enum TimestampKind {
  /// <summary>
  /// Populated when dispatcher.SendAsync() or PublishAsync() is called.
  /// Captures the moment the message enters the dispatch pipeline.
  /// </summary>
  SentAt = 0,

  /// <summary>
  /// Populated after the message is written to the outbox and committed.
  /// Captures the moment the message is durably stored for delivery.
  /// Only fires for distributed messages (not local-only dispatch).
  /// </summary>
  QueuedAt = 1,

  /// <summary>
  /// Populated when the message arrives at the destination inbox.
  /// Captures the moment the message is received from transport.
  /// Only fires for distributed messages (not local-only dispatch).
  /// </summary>
  DeliveredAt = 2
}
