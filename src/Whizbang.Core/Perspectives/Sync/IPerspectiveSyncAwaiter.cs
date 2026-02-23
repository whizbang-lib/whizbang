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
}
