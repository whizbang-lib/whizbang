namespace Whizbang.Core.Attributes;

/// <summary>
/// Specifies the kind of message identifier to auto-populate on a message property.
/// Values are sourced from the MessageEnvelope during message dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Use with <see cref="PopulateFromIdentifierAttribute"/> to automatically capture
/// message identifiers for correlation, causation tracking, and saga patterns.
/// </para>
/// <para>
/// <list type="bullet">
/// <item><description><see cref="MessageId"/> - Unique identifier for the current message</description></item>
/// <item><description><see cref="CorrelationId"/> - Links all messages in a workflow/saga</description></item>
/// <item><description><see cref="CausationId"/> - ID of the message that caused this one</description></item>
/// <item><description><see cref="StreamId"/> - The stream/aggregate this message belongs to</description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateFromIdentifierAttributeTests.cs</tests>
public enum IdentifierKind {
  /// <summary>
  /// Populated with the current message's unique identifier.
  /// Each message has a unique ID generated at dispatch time.
  /// </summary>
  MessageId = 0,

  /// <summary>
  /// Populated with the correlation identifier that links all messages in a workflow.
  /// Shared by all messages in a saga or distributed transaction.
  /// Essential for distributed tracing and workflow debugging.
  /// </summary>
  CorrelationId = 1,

  /// <summary>
  /// Populated with the identifier of the message that caused this one.
  /// Forms a causal chain for understanding message relationships.
  /// Useful for debugging and reconstructing event sequences.
  /// </summary>
  CausationId = 2,

  /// <summary>
  /// Populated with the stream/aggregate identifier this message belongs to.
  /// The same value as marked with [StreamId] attribute, but auto-populated.
  /// </summary>
  StreamId = 3
}
