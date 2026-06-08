namespace Whizbang.Core.Messaging;

/// <summary>
/// Thrown by the storage-time guard when a producer attempts to write an
/// outbox / inbox / perspective row with <c>stream_id = </c>
/// <see cref="System.Guid.Empty"/> while
/// <see cref="Whizbang.Core.Configuration.EmptyStreamIdPolicy.Reject"/> is in
/// effect. Carries the offending <c>message_id</c> and <c>message_type</c>
/// so producers debugging via stack trace can locate the exact bad row.
/// </summary>
/// <remarks>
/// See <see cref="Whizbang.Core.Configuration.EmptyStreamIdPolicy"/> for the
/// motivation (slot-3 forensic, Jun 2026).
/// </remarks>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "EmptyStreamIdException always carries MessageId + MessageType — those are required for the exception to be useful. The standard parameterless / message-only / message-and-inner constructors would defeat the purpose; the typed (Guid, string) constructor is the only valid shape.")]
public sealed class EmptyStreamIdException : System.Exception {
  /// <summary>The <c>message_id</c> of the row whose <c>stream_id</c> was Empty.</summary>
  public System.Guid MessageId { get; }

  /// <summary>The <c>message_type</c> (AQN) of the offending row's payload.</summary>
  public string MessageType { get; }

  /// <summary>
  /// Constructs with the offending <paramref name="messageId"/> and
  /// <paramref name="messageType"/>. The exception's <c>Message</c> property is
  /// self-describing so log readers don't need to inspect the structured fields.
  /// </summary>
  public EmptyStreamIdException(System.Guid messageId, string messageType)
    : base(_format(messageId, messageType)) {
    MessageId = messageId;
    MessageType = messageType;
  }

  private static string _format(System.Guid messageId, string messageType) =>
    $"Producer attempted to write {messageType} (message_id={messageId}) with stream_id=Guid.Empty (00000000-0000-0000-0000-000000000000). " +
    $"Empty stream_id is rejected under EmptyStreamIdPolicy.Reject — pass null for singleton-stream messages or a real stream identity. " +
    $"See operations/configuration/empty-stream-id-policy for migration guidance.";
}
