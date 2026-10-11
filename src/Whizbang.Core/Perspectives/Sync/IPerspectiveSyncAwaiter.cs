// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs:PerspectiveSyncAwaiter_WaitAsync_CompletesWhenDatabaseReturnsSyncedAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs:PerspectiveSyncAwaiter_IsCaughtUpAsync_WithUnprocessedEvents_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs:WaitForStreamAsync_CrossScope_WithPendingEvents_WaitsUntilProcessedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_RegistersPerspectiveSyncAwaiter_AsScopedAsync</tests>
public interface IPerspectiveSyncAwaiter : IAwaiterIdentity {
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
  /// <docs>fundamentals/perspectives/perspective-sync#stream-based</docs>
  Task<SyncResult> WaitForStreamAsync(
      Type perspectiveType,
      Guid streamId,
      Type[]? eventTypes,
      TimeSpan timeout,
      Guid? eventIdToAwait = null,
      CancellationToken ct = default);

  /// <summary>
  /// Waits until a local perspective has applied an event, whoever published it: an event emitted here, one
  /// that arrived over the transport from another service, or a collective event the collective sink applied.
  /// </summary>
  /// <remarks>
  /// Answered from the applied-event ledger, which every instance shares, and woken early by an apply committed
  /// in this process. An event that has not reached this service yet is waited for, not reported as synced.
  /// </remarks>
  /// <param name="perspectiveType">The perspective whose read model must have the event.</param>
  /// <param name="eventId">The event's id (the envelope's message id, the same in every service).</param>
  /// <param name="timeout">The longest to wait.</param>
  /// <param name="ct">Cancels the wait; a canceled wait throws rather than reporting a timeout.</param>
  /// <returns>
  /// <see cref="SyncOutcome.Synced"/> once applied, <see cref="SyncOutcome.NoPendingEvents"/> when the
  /// perspective has nothing to apply for the event, or <see cref="SyncOutcome.TimedOut"/>.
  /// </returns>
  /// <docs>fundamentals/perspectives/perspective-sync#cross-service</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/PerspectiveSyncAwaiterAppliedTests.cs</tests>
  Task<SyncResult> WaitForAppliedAsync(
      Type perspectiveType,
      Guid eventId,
      TimeSpan timeout,
      CancellationToken ct = default) =>
    throw new NotSupportedException($"{GetType().Name} does not implement WaitForAppliedAsync.");

  /// <summary>
  /// Waits until a local perspective has applied the event at a position in a stream, whoever published it.
  /// </summary>
  /// <param name="perspectiveType">The perspective whose read model must have the event.</param>
  /// <param name="streamId">The stream.</param>
  /// <param name="streamPosition">
  /// The event's position in THIS service's copy of the stream: the per-stream version the local event store
  /// assigned (1 for the first event). A service that received the stream numbers it itself.
  /// </param>
  /// <param name="timeout">The longest to wait.</param>
  /// <param name="ct">Cancels the wait; a canceled wait throws rather than reporting a timeout.</param>
  /// <returns>As <see cref="WaitForAppliedAsync(Type, Guid, TimeSpan, CancellationToken)"/>.</returns>
  /// <docs>fundamentals/perspectives/perspective-sync#cross-service</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/PerspectiveSyncAwaiterAppliedTests.cs</tests>
  Task<SyncResult> WaitForAppliedAsync(
      Type perspectiveType,
      Guid streamId,
      int streamPosition,
      TimeSpan timeout,
      CancellationToken ct = default) =>
    throw new NotSupportedException($"{GetType().Name} does not implement WaitForAppliedAsync.");
}
