namespace Whizbang.Core.Routing;

/// <summary>
/// Why a message was (or was not) skipped at one of the discard gates.
/// </summary>
/// <remarks>
/// Used as an OTel metric tag value and as the structured-log <c>{Reason}</c> field —
/// the enum names are the wire format. Don't rename without a coordinated dashboard
/// update.
/// </remarks>
/// <docs>internals/message-discard-policy</docs>
public enum MessageDiscardReason {
  /// <summary>Default — no reason to skip; the message proceeds.</summary>
  None,

  /// <summary>
  /// Transport-receive: this service has no receptor or perspective registered for the
  /// payload CLR type. Expected for cross-domain subscribers that consume a subset of
  /// a topic's event types. Routine.
  /// </summary>
  NoLocalConsumer,

  /// <summary>
  /// Outbox publish: the catalog reports zero subscribers anywhere for this event type
  /// (i.e. no service in the system has a receptor or perspective for it). Routine.
  /// </summary>
  NoKnownConsumer,

  /// <summary>
  /// Inbox dispatch: the row references a handler the active registry no longer
  /// exposes — typically after a rolling deploy removed a receptor that an in-flight
  /// row was bound to. Informational; not actionable.
  /// </summary>
  RegistryChanged,

  /// <summary>
  /// Subscription points at a topic this service isn't supposed to own. Indicates a
  /// configuration mistake somewhere upstream.
  /// </summary>
  DomainNotOwned,
}
