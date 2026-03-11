namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Waits for events to be fully processed by all registered perspectives.
/// </summary>
/// <remarks>
/// <para>
/// This service provides a way to wait until specific events have been processed
/// by ALL perspectives that are tracking them, not just one.
/// </para>
/// <para>
/// <strong>Contrast with <see cref="IPerspectiveSyncAwaiter"/>:</strong>
/// </para>
/// <list type="bullet">
///   <item><description><see cref="IPerspectiveSyncAwaiter"/> waits for a SPECIFIC perspective to process events</description></item>
///   <item><description><see cref="IEventCompletionAwaiter"/> waits for ALL perspectives to process events</description></item>
/// </list>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// // Wait for specific events to be fully processed by all perspectives
/// var result = await awaiter.WaitForEventsAsync(
///     eventIds,
///     TimeSpan.FromSeconds(30),
///     cancellationToken);
///
/// if (result) {
///     // All perspectives have processed these events
/// }
/// </code>
/// </remarks>
/// <docs>core-concepts/perspectives/event-completion</docs>
public interface IEventCompletionAwaiter : IAwaiterIdentity {
  /// <summary>
  /// Waits for specific events to be processed by ALL perspectives.
  /// Returns when no perspectives are still tracking any of the specified events.
  /// </summary>
  /// <param name="eventIds">The event IDs to wait for.</param>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True if all events were fully processed within timeout, false otherwise.</returns>
  /// <remarks>
  /// <para>
  /// This method returns <c>true</c> when:
  /// </para>
  /// <list type="bullet">
  ///   <item><description>All perspectives have called <see cref="ISyncEventTracker.MarkProcessedByPerspective"/> for all event IDs</description></item>
  ///   <item><description>The event IDs were never tracked (nothing to wait for)</description></item>
  ///   <item><description>The event list is null or empty</description></item>
  /// </list>
  /// <para>
  /// Returns <c>false</c> when:
  /// </para>
  /// <list type="bullet">
  ///   <item><description>The timeout expires before all perspectives finish</description></item>
  ///   <item><description>The cancellation token is cancelled</description></item>
  /// </list>
  /// </remarks>
  Task<bool> WaitForEventsAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Checks if specific events have been fully processed by all perspectives.
  /// </summary>
  /// <param name="eventIds">The event IDs to check.</param>
  /// <returns>True if all events have been processed by all perspectives, or if no events are being tracked.</returns>
  /// <remarks>
  /// This is a non-blocking check that returns immediately.
  /// Use <see cref="WaitForEventsAsync"/> to wait for completion.
  /// </remarks>
  bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds);
}
