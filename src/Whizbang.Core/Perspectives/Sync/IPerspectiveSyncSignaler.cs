namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Provides low-latency signaling when perspective cursors are updated.
/// </summary>
/// <remarks>
/// <para>
/// This service enables fast notification when perspectives are updated,
/// reducing the need for polling in sync awaiter implementations.
/// </para>
/// <para>
/// <strong>Implementations:</strong>
/// </para>
/// <list type="bullet">
/// <item><see cref="LocalSyncSignaler"/> - In-process channel-based signaling</item>
/// </list>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncSignalerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncSignalerTests.cs:LocalSyncSignaler_SignalCheckpointUpdated_NotifiesSubscribersAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncSignalerTests.cs:LocalSyncSignaler_Subscribe_ReturnsDisposableAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_RegistersPerspectiveSyncSignaler_AsSingletonAsync</tests>
public interface IPerspectiveSyncSignaler : IDisposable {
  /// <summary>
  /// Signals that a perspective cursor has been updated.
  /// </summary>
  /// <param name="perspectiveType">The type of the perspective.</param>
  /// <param name="streamId">The stream ID that was processed.</param>
  /// <param name="lastEventId">The ID of the last event processed.</param>
  /// <remarks>
  /// Called by PerspectiveWorker after checkpoint is saved.
  /// </remarks>
  void SignalCheckpointUpdated(Type perspectiveType, Guid streamId, Guid lastEventId);

  /// <summary>
  /// Subscribes to checkpoint update signals for a specific perspective.
  /// </summary>
  /// <param name="perspectiveType">The perspective type to subscribe to.</param>
  /// <param name="onSignal">The handler called when a signal is received.</param>
  /// <returns>A disposable subscription that can be used to unsubscribe.</returns>
  IDisposable Subscribe(Type perspectiveType, Action<PerspectiveCursorSignal> onSignal);
}
