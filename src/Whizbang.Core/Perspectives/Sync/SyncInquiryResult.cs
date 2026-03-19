namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Result of a sync inquiry from the database.
/// </summary>
/// <remarks>
/// <para>
/// Returned by the batch function after querying <c>wh_perspective_events</c>
/// to determine how many events are still pending for a perspective.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// if (result.IsFullySynced) {
///     // All events have been processed
///     var projection = await lens.GetAsync&lt;OrderPerspective&gt;(orderId);
/// }
/// </code>
/// <para>
/// <strong>Explicit Event Tracking:</strong>
/// When <see cref="ExpectedEventIds"/> is set, <see cref="IsFullySynced"/> returns true
/// only when ALL expected events are in <see cref="ProcessedEventIds"/>. This prevents
/// false positives when events haven't appeared in <c>wh_perspective_events</c> yet.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncInquiryTests.cs</tests>
public sealed record SyncInquiryResult {
  /// <summary>
  /// Gets the correlation ID from the inquiry.
  /// </summary>
  public required Guid InquiryId { get; init; }

  /// <summary>
  /// Gets the stream ID that was queried.
  /// </summary>
  public Guid StreamId { get; init; }

  /// <summary>
  /// Gets the number of events still pending (processed_at IS NULL).
  /// </summary>
  public required int PendingCount { get; init; }

  /// <summary>
  /// Gets the number of events that have been processed (processed_at IS NOT NULL).
  /// </summary>
  public int ProcessedCount { get; init; }

  /// <summary>
  /// Gets a value indicating whether all requested events have been processed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// When <see cref="ExpectedEventIds"/> is set (explicit tracking):
  /// Returns <c>true</c> only when ALL expected event IDs are in <see cref="ProcessedEventIds"/>.
  /// </para>
  /// <para>
  /// When <see cref="ExpectedEventIds"/> is null or empty (legacy stream-wide query):
  /// Falls back to <c>PendingCount == 0</c>.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/perspectives/perspective-sync#is-fully-synced</docs>
  public bool IsFullySynced => ExpectedEventIds is { Length: > 0 }
      ? ProcessedEventIds is not null && ExpectedEventIds.All(id => ProcessedEventIds.Contains(id))
      : PendingCount == 0;

  /// <summary>
  /// Gets the pending event IDs (only populated if IncludePendingEventIds was true in the inquiry).
  /// </summary>
  public Guid[]? PendingEventIds { get; init; }

  /// <summary>
  /// Gets the processed event IDs (only populated if IncludeProcessedEventIds was true in the inquiry).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Contains the event IDs that have been processed by the perspective
  /// (i.e., have <c>processed_at IS NOT NULL</c> in <c>wh_perspective_events</c>).
  /// </para>
  /// <para>
  /// Used with <see cref="ExpectedEventIds"/> to determine if ALL expected events
  /// have been processed, even if some events haven't reached <c>wh_perspective_events</c> yet.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/perspectives/perspective-sync#explicit-event-tracking</docs>
  public Guid[]? ProcessedEventIds { get; init; }

  /// <summary>
  /// Gets the expected event IDs that must be processed for sync to be complete.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Set by the caller (e.g., <see cref="PerspectiveSyncAwaiter"/>) based on events
  /// tracked by <see cref="IScopedEventTracker"/>. When set, <see cref="IsFullySynced"/>
  /// checks that ALL expected events are in <see cref="ProcessedEventIds"/>.
  /// </para>
  /// <para>
  /// This prevents the false positive where <c>PendingCount == 0</c> because the events
  /// haven't reached <c>wh_perspective_events</c> yet (still in outbox).
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/perspectives/perspective-sync#explicit-event-tracking</docs>
  public Guid[]? ExpectedEventIds { get; init; }
}
