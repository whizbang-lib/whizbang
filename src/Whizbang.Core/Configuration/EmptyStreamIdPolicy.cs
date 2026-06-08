namespace Whizbang.Core.Configuration;

/// <summary>
/// Governs how Whizbang handles outbox / inbox / perspective rows whose
/// <c>stream_id</c> is the all-zeros UUID (<see cref="System.Guid.Empty"/>).
/// </summary>
/// <remarks>
/// <para>
/// Slot-3 forensic (Jun 2026): JDX BFF accumulated a <c>RemoveShellUserCommand</c>
/// outbox row with <c>stream_id = 00000000-0000-0000-0000-000000000000</c> (the
/// real zero-UUID, not NULL). The C# coordinator's <c>r.StreamId ?? r.WorkId</c>
/// coalesce only catches NULL; <see cref="System.Guid.Empty"/> slipped through,
/// then the <c>Where(g =&gt; g != Guid.Empty)</c> filter dropped it. ClaimWorker
/// bumped <c>attempts</c> on every claim cycle (~996 increments observed) but
/// never wrote to the drain channel — silently stuck forever, no DLQ promotion,
/// no error captured, no log emitted.
/// </para>
/// <para>
/// This enum is the structural fix. The default <see cref="Reject"/> closes the
/// producer-side hole; the consumer-side behavior (Empty→<c>WorkId</c> fallback +
/// Warning) is unconditional so rows that already landed before the producer
/// fix shipped can still drain.
/// </para>
/// </remarks>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
public enum EmptyStreamIdPolicy {
  /// <summary>
  /// Secure default. Storage-time SQL guard throws
  /// <see cref="Whizbang.Core.Messaging.EmptyStreamIdException"/> when a producer
  /// attempts to INSERT a row with <c>stream_id = Guid.Empty</c>. The producer's
  /// commit fails with a typed exception naming the offending <c>message_id</c>
  /// and <c>message_type</c>, so the bug is surfaced where it was introduced —
  /// not after the resulting row spins silently in <c>wh_outbox</c>.
  /// </summary>
  Reject = 0,

  /// <summary>
  /// Lenient mode for in-flight migrations. Storage accepts the row with
  /// Empty <c>stream_id</c>; the coordinator-side coalesce treats Empty the
  /// same as NULL — fall back to <c>WorkId</c> as the singleton-stream
  /// identity, emit a Warning naming the row, and process normally. Use when
  /// you have legacy producers you can't fix immediately but don't want to
  /// block on; track the Warning count to gate the eventual flip to
  /// <see cref="Reject"/>.
  /// </summary>
  FallbackToMessageId = 1,

  /// <summary>
  /// Move-to-DLQ semantics. Storage accepts the row; the coordinator moves it
  /// to <c>wh_dead_letters</c> with
  /// <see cref="Whizbang.Core.Messaging.MessageFailureReason.EmptyStreamId"/>
  /// instead of attempting to drain. Use when you want forensic preservation of
  /// every bad row plus the option to recover them after the producer fix
  /// (recovery normalizes Empty→NULL).
  /// </summary>
  DeadLetter = 2,

  /// <summary>
  /// Hard-purge. Storage accepts the row; the coordinator DELETEs it without
  /// publishing and emits an Error log carrying the
  /// <see cref="Whizbang.Core.Messaging.MessageFailureReason.EmptyStreamId"/>
  /// code so operators can grep + alert. Use only for known-spammy producers
  /// you've already given up on; data loss is permanent.
  /// </summary>
  Purge = 3
}
