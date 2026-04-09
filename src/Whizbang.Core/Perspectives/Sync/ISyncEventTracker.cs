namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Singleton service that tracks events awaiting perspective sync.
/// Bridges request scopes within the same microservice instance.
/// </summary>
/// <remarks>
/// <para>
/// This tracker captures events at emit time (before they reach the database),
/// enabling cross-scope synchronization where Request 2 can wait for events
/// emitted by Request 1.
/// </para>
/// <para>
/// The implementation must be thread-safe for concurrent access from multiple
/// request scopes simultaneously.
/// </para>
/// <para>
/// Wait methods accept an optional <c>awaiterId</c> parameter that keys waiter
/// registrations. This enables precise cleanup via <see cref="UnregisterAwaiter"/>
/// when an awaiter is cancelled — without affecting other awaiters waiting on
/// the same events.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#event-tracking</docs>
public interface ISyncEventTracker {
  /// <summary>
  /// Track an event that needs to be awaited for perspective sync.
  /// Called immediately when event is emitted (before database).
  /// </summary>
  /// <param name="eventType">The type of the event being tracked.</param>
  /// <param name="eventId">The unique identifier of the event.</param>
  /// <param name="streamId">The stream the event belongs to.</param>
  /// <param name="perspectiveName">The perspective awaiting this event.</param>
  void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName);

  /// <summary>
  /// Get pending events for a stream that match the given event types and perspective.
  /// </summary>
  /// <param name="streamId">The stream to query.</param>
  /// <param name="perspectiveName">The perspective name to filter by.</param>
  /// <param name="eventTypes">Optional event types to filter by. If null or empty, returns all types.</param>
  /// <returns>A read-only list of tracked events matching the criteria.</returns>
  IReadOnlyList<TrackedSyncEvent> GetPendingEvents(
      Guid streamId,
      string perspectiveName,
      Type[]? eventTypes = null);

  /// <summary>
  /// Mark events as processed (called when ProcessWorkBatch confirms completion).
  /// </summary>
  /// <param name="eventIds">The event IDs to mark as processed.</param>
  void MarkProcessed(IEnumerable<Guid> eventIds);

  /// <summary>
  /// Get all tracked event IDs (to send to ProcessWorkBatch for completion check).
  /// </summary>
  /// <returns>A read-only list of all currently tracked event IDs.</returns>
  IReadOnlyList<Guid> GetAllTrackedEventIds();

  /// <summary>
  /// Waits for specific events to be marked as processed.
  /// Returns when ALL specified events are processed, or when the timeout expires.
  /// </summary>
  /// <param name="eventIds">The event IDs to wait for.</param>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="awaiterId">Optional awaiter ID for per-awaiter cleanup. Auto-generated if null.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True if all events were processed within timeout, false otherwise.</returns>
  Task<bool> WaitForEventsAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      Guid? awaiterId = null,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Mark events as processed by a specific perspective.
  /// Only removes the entry for the specified perspective, not all perspectives.
  /// </summary>
  /// <param name="eventIds">The event IDs to mark as processed.</param>
  /// <param name="perspectiveName">The perspective that processed these events.</param>
  /// <remarks>
  /// <para>
  /// Use <see cref="WaitForPerspectiveEventsAsync"/> to wait for a specific perspective,
  /// or <see cref="WaitForAllPerspectivesAsync"/> to wait for all perspectives.
  /// </para>
  /// <para>
  /// Unlike <see cref="MarkProcessed"/>, this method only removes the entry for the
  /// specified perspective. The event remains tracked for other perspectives until
  /// they also call this method.
  /// </para>
  /// </remarks>
  void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName);

  /// <summary>
  /// Waits for specific events to be processed by a SPECIFIC perspective.
  /// Signals when the (eventId, perspectiveName) entries are removed.
  /// </summary>
  /// <param name="eventIds">The event IDs to wait for.</param>
  /// <param name="perspectiveName">The perspective to wait for.</param>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="awaiterId">Optional awaiter ID for per-awaiter cleanup. Auto-generated if null.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True if the perspective processed all events within timeout, false otherwise.</returns>
  /// <remarks>
  /// Used by <see cref="IPerspectiveSyncAwaiter"/> to wait for a specific perspective
  /// to process events, without waiting for other perspectives.
  /// </remarks>
  Task<bool> WaitForPerspectiveEventsAsync(
      IReadOnlyList<Guid> eventIds,
      string perspectiveName,
      TimeSpan timeout,
      Guid? awaiterId = null,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Waits for specific events to be processed by ALL perspectives.
  /// Signals only when NO entries remain for any of the specified event IDs.
  /// </summary>
  /// <param name="eventIds">The event IDs to wait for.</param>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="awaiterId">Optional awaiter ID for per-awaiter cleanup. Auto-generated if null.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True if all perspectives processed all events within timeout, false otherwise.</returns>
  /// <remarks>
  /// Used by <see cref="IEventCompletionAwaiter"/> to wait for all perspectives
  /// to fully process events before returning from RPC calls.
  /// </remarks>
  Task<bool> WaitForAllPerspectivesAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      Guid? awaiterId = null,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Unregisters all waiter entries for a specific awaiter, cancelling any pending TCS.
  /// Called when an awaiter is cancelled or disposed to prevent stale TCS accumulation.
  /// </summary>
  /// <param name="awaiterId">The awaiter ID whose entries should be removed and cancelled.</param>
  void UnregisterAwaiter(Guid awaiterId);

  /// <summary>
  /// Removes tracked events older than <paramref name="maxAge"/> and signals any waiters
  /// registered for the removed entries. Prevents unbounded memory growth when events are
  /// never processed (e.g., perspective failures, unregistered types).
  /// </summary>
  /// <param name="maxAge">Maximum age for tracked events. Entries older than this are removed.</param>
  /// <returns>The number of entries removed.</returns>
  int CleanupStaleEntries(TimeSpan maxAge);
}
