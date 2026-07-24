namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Tracks events emitted within the current scope for local synchronization.
/// </summary>
/// <remarks>
/// <para>
/// This service is scoped per-request/operation and tracks all events emitted
/// during that scope. It enables local (in-memory) lookup for perspective synchronization.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// // Called by Dispatcher when events are emitted
/// tracker.TrackEmittedEvent(streamId, typeof(OrderCreatedEvent), eventId);
///
/// // Query tracked events
/// var events = tracker.GetEmittedEvents(filter);
///
/// // Check if all tracked events have been processed
/// var allProcessed = tracker.AreAllProcessed(filter, processedEventIds);
/// </code>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs</tests>
public interface IScopedEventTracker {
  /// <summary>
  /// Tracks an event that has been emitted in the current scope.
  /// </summary>
  /// <param name="streamId">The stream ID the event belongs to.</param>
  /// <param name="eventType">The type of the event.</param>
  /// <param name="eventId">The unique identifier of the event.</param>
  /// <remarks>
  /// Called by the Dispatcher when events are published or cascaded.
  /// </remarks>
  void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId);

  /// <summary>
  /// Gets all events emitted in the current scope.
  /// </summary>
  /// <returns>A read-only list of tracked events.</returns>
  IReadOnlyList<TrackedEvent> GetEmittedEvents();

  /// <summary>
  /// Gets events emitted in the current scope that match the specified filter.
  /// </summary>
  /// <param name="filter">The filter to apply.</param>
  /// <returns>A read-only list of matching tracked events.</returns>
  IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter);

  /// <summary>
  /// Checks if all tracked events matching the filter have been processed.
  /// </summary>
  /// <param name="filter">The filter to apply to tracked events.</param>
  /// <param name="processedEventIds">The set of event IDs that have been processed.</param>
  /// <returns>
  /// <c>true</c> if all matching events are in the processed set; otherwise, <c>false</c>.
  /// Returns <c>true</c> if no events match the filter.
  /// </returns>
  bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds);
}
