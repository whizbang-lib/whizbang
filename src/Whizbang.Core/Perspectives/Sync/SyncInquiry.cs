using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Query to check if specific events have been processed by a perspective.
/// </summary>
/// <remarks>
/// <para>
/// Used to ask the database whether a perspective has caught up with specific events.
/// Sync inquiries are passed to the batch function and answered by querying
/// the <c>wh_perspective_events</c> table.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// var inquiry = new SyncInquiry {
///     StreamId = orderId,
///     PerspectiveName = "OrderPerspective",
///     EventIds = [eventId1, eventId2]
/// };
/// </code>
/// </remarks>
/// <docs>perspectives/sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncInquiryTests.cs</tests>
public sealed record SyncInquiry {
  /// <summary>
  /// Gets the stream ID to check.
  /// </summary>
  /// <remarks>
  /// Derived from the event's <c>[StreamKey]</c> or <c>[AggregateId]</c> attribute.
  /// </remarks>
  public required Guid StreamId { get; init; }

  /// <summary>
  /// Gets the perspective name to check.
  /// </summary>
  public required string PerspectiveName { get; init; }

  /// <summary>
  /// Gets optional specific event IDs to check.
  /// </summary>
  /// <remarks>
  /// When <c>null</c>, checks all events for the stream.
  /// When specified, only checks these specific events.
  /// </remarks>
  public Guid[]? EventIds { get; init; }

  /// <summary>
  /// Gets optional event type filter.
  /// </summary>
  /// <remarks>
  /// When <c>null</c>, checks all event types.
  /// When specified, only checks events of these types (full type names).
  /// </remarks>
  public string[]? EventTypeFilter { get; init; }

  /// <summary>
  /// Gets a value indicating whether to include pending event IDs in the result.
  /// </summary>
  /// <remarks>
  /// Set to <c>true</c> for debugging purposes. Defaults to <c>false</c> for performance.
  /// </remarks>
  /// <value>Default: <c>false</c>.</value>
  public bool IncludePendingEventIds { get; init; }

  /// <summary>
  /// Gets a value indicating whether to include processed event IDs in the result.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Set to <c>true</c> when using explicit event ID tracking for sync.
  /// This enables the caller to verify that ALL expected events have been processed,
  /// even if they haven't appeared in <c>wh_perspective_events</c> yet.
  /// </para>
  /// <para>
  /// Defaults to <c>false</c> for performance.
  /// </para>
  /// </remarks>
  /// <value>Default: <c>false</c>.</value>
  /// <docs>core-concepts/perspectives/perspective-sync#explicit-event-tracking</docs>
  public bool IncludeProcessedEventIds { get; init; }

  /// <summary>
  /// Gets a value indicating whether to discover pending events from the outbox.
  /// </summary>
  /// <remarks>
  /// <para>
  /// When <c>true</c> and <see cref="EventIds"/> is null, the SQL will query the outbox
  /// to find events of the specified <see cref="EventTypeFilter"/> types on this stream
  /// that haven't been processed by the perspective yet.
  /// </para>
  /// <para>
  /// This enables cross-scope/cross-request sync where the caller doesn't know the
  /// specific EventIds to wait for, only the event types.
  /// </para>
  /// <para>
  /// Defaults to <c>false</c> for backwards compatibility.
  /// </para>
  /// </remarks>
  /// <value>Default: <c>false</c>.</value>
  /// <docs>core-concepts/perspectives/perspective-sync#cross-scope-sync</docs>
  public bool DiscoverPendingFromOutbox { get; init; }

  /// <summary>
  /// Gets the correlation ID to match request with response.
  /// </summary>
  /// <remarks>
  /// Auto-generated if not specified.
  /// </remarks>
  public Guid InquiryId { get; init; } = TrackedGuid.NewMedo();
}
