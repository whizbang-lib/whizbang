namespace Whizbang.Core.Messaging;

/// <summary>
/// Forensic record returned by
/// <see cref="IWorkCoordinator.FindStuckOutboxRowsAsync"/> and
/// <see cref="IWorkCoordinator.FindStuckInboxRowsAsync"/> describing a row
/// that has been claimed past <c>MaxOutboxAttempts</c> / <c>MaxInboxAttempts</c>
/// without being processed.
/// </summary>
/// <remarks>
/// <para>
/// v0.657 slice 5: the structural canary for the "row claimed but never
/// drained" bug class. Slices 1-4 close the specific Empty-stream instance
/// of this class; this surface lets the maintenance worker log a Warning per
/// row exhibiting the same symptom — regardless of root cause.
/// </para>
/// <para>
/// All four fields are forensic-grade — operators investigating the row
/// should be able to look it up by <see cref="MessageId"/>, identify the
/// producer via <see cref="MessageType"/>, see how long it's been
/// accumulating via <see cref="Attempts"/>, and correlate with deploy
/// boundaries via <see cref="ClaimedSince"/>.
/// </para>
/// </remarks>
/// <docs>operations/observability/stuck-row-sentinel</docs>
public sealed record StuckRow {
  /// <summary>The <c>message_id</c> of the stuck row.</summary>
  public required Guid MessageId { get; init; }

  /// <summary>The <c>message_type</c> (AQN) of the row's payload.</summary>
  public required string MessageType { get; init; }

  /// <summary>The row's <c>stream_id</c>, or NULL for singleton-stream messages.</summary>
  public required Guid? StreamId { get; init; }

  /// <summary>The current attempts count — exceeds <c>MaxOutboxAttempts</c> / <c>MaxInboxAttempts</c>.</summary>
  public required int Attempts { get; init; }

  /// <summary>
  /// The row's <c>created_at</c> (outbox) or <c>received_at</c> (inbox) —
  /// when the row first landed. Compare against deploy timestamps to identify
  /// regression boundaries.
  /// </summary>
  public required DateTime ClaimedSince { get; init; }
}
