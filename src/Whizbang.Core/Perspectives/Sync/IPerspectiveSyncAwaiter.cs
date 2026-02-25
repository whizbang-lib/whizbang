namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Core service for waiting until perspectives are caught up with pending events.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// // Wait for all events in current scope
/// var result = await awaiter.WaitAsync(
///     typeof(OrderPerspective),
///     SyncFilter.CurrentScope().Local(),
///     cancellationToken);
///
/// if (result.Outcome == SyncOutcome.Synced) {
///     // Perspective is now caught up
/// }
/// </code>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
public interface IPerspectiveSyncAwaiter {
  /// <summary>
  /// Waits until perspectives are caught up per the sync options.
  /// </summary>
  /// <param name="perspectiveType">The type of the perspective to wait for.</param>
  /// <param name="options">The synchronization options including filter, timeout, etc.</param>
  /// <param name="ct">A cancellation token.</param>
  /// <returns>The result of the sync operation.</returns>
  Task<SyncResult> WaitAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default);

  /// <summary>
  /// Checks if perspectives are caught up without waiting.
  /// </summary>
  /// <param name="perspectiveType">The type of the perspective to check.</param>
  /// <param name="options">The synchronization options including filter.</param>
  /// <param name="ct">A cancellation token.</param>
  /// <returns><c>true</c> if caught up; otherwise, <c>false</c>.</returns>
  Task<bool> IsCaughtUpAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default);

  /// <summary>
  /// Waits for all pending events on a stream to be processed by a perspective.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method waits for specific events on a stream to be processed by the perspective.
  /// It supports two modes:
  /// </para>
  /// <list type="bullet">
  /// <item>
  /// <description>
  /// <strong>Explicit event tracking:</strong> When <paramref name="eventIdToAwait"/> is provided,
  /// the method waits for that specific event to be processed. This is the preferred mode for
  /// attribute-based sync where the incoming event's ID is known.
  /// </description>
  /// </item>
  /// <item>
  /// <description>
  /// <strong>Scope-based tracking:</strong> When <paramref name="eventIdToAwait"/> is null and
  /// <c>IScopedEventTracker</c> is available, the method uses events tracked in the current scope.
  /// </description>
  /// </item>
  /// </list>
  /// <para>
  /// <strong>Cross-scope sync:</strong> Unlike <see cref="WaitAsync"/>, this method works
  /// correctly across scopes when the incoming event ID is provided:
  /// </para>
  /// <code>
  /// // Scope A: Command handler emits event
  /// await outbox.PublishAsync(new OrderCreatedEvent { OrderId = orderId });
  ///
  /// // Scope B: Receptor with [AwaitPerspectiveSync] - passes incoming event ID
  /// var result = await awaiter.WaitForStreamAsync(
  ///     typeof(OrderProjection),
  ///     orderId,
  ///     eventTypes: null,
  ///     timeout: TimeSpan.FromSeconds(5),
  ///     eventIdToAwait: incomingEventId); // Key: pass the event we're waiting for
  /// </code>
  /// </remarks>
  /// <param name="perspectiveType">The type of the perspective to wait for.</param>
  /// <param name="streamId">The stream ID to wait for (extracted from message).</param>
  /// <param name="eventTypes">Optional event types to filter. If null, waits for ALL events on the stream.</param>
  /// <param name="timeout">The maximum time to wait.</param>
  /// <param name="eventIdToAwait">
  /// Optional specific event ID to wait for. When provided, the sync waits for THIS event
  /// to be processed, enabling correct cross-scope sync for attribute-based sync scenarios.
  /// </param>
  /// <param name="ct">A cancellation token.</param>
  /// <returns>The result of the sync operation.</returns>
  /// <docs>core-concepts/perspectives/perspective-sync#stream-based</docs>
  Task<SyncResult> WaitForStreamAsync(
      Type perspectiveType,
      Guid streamId,
      Type[]? eventTypes,
      TimeSpan timeout,
      Guid? eventIdToAwait = null,
      CancellationToken ct = default);
}
