using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="PerspectiveSyncAwaiter"/> integration with <see cref="ISyncEventTracker"/>.
/// </summary>
/// <remarks>
/// <para>
/// These tests verify the cross-scope sync scenario where:
/// 1. Request A emits an event and tracks it in the singleton ISyncEventTracker
/// 2. Request B handles a command with [AwaitPerspectiveSync]
/// 3. Request B uses ISyncEventTracker to find tracked events
/// 4. The sync waits for those specific tracked event IDs
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync#tracker-integration</docs>
[NotInParallel("SyncTests")]
public class PerspectiveSyncAwaiterTrackerTests {
  // Dummy perspective type for testing
  private sealed class TestPerspective { }

  // Sample event types for testing
  private sealed record TestEventA;
  private sealed record TestEventB;
  private sealed record TestEventC;

  // ==========================================================================
  // Constructor tests with ISyncEventTracker
  // ==========================================================================

  [Test]
  public async Task Constructor_AcceptsBothTrackersAsync() {
    // Arrange
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var scopedTracker = new ScopedEventTracker();
    var syncEventTracker = new SyncEventTracker();

    // Act - both trackers can be provided
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator,
        clock,
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker,
        tracker: scopedTracker);

    // Assert
    await Assert.That(awaiter).IsNotNull();
  }

  [Test]
  public async Task Constructor_AcceptsOnlySyncEventTrackerAsync() {
    // Arrange
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var syncEventTracker = new SyncEventTracker();

    // Act - only ISyncEventTracker, no IScopedEventTracker
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator,
        clock,
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker);

    // Assert
    await Assert.That(awaiter).IsNotNull();
  }

  // ==========================================================================
  // WaitForStreamAsync with ISyncEventTracker tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_WithTrackedEvents_UsesTrackerEventIdsAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Setup tracker with a tracked event
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    // With event-driven waiting, the coordinator is NOT called when tracker has events
    // Instead, we wait for MarkProcessed to be called on the tracker
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessedByPerspective after a short delay
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - should have waited for event-driven completion
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task WaitForStreamAsync_TrackerFiltersEventTypesAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventIdA = Guid.NewGuid();
    var eventIdB = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Track two different event types
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(TestEventA), eventIdA, streamId, perspectiveName);
    syncEventTracker.TrackEvent(typeof(TestEventB), eventIdB, streamId, perspectiveName);

    // With event-driven waiting, coordinator is NOT called when tracker has events
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessedByPerspective for eventIdA only
    // This verifies that filtering works - only eventIdA is waited for (not eventIdB)
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessedByPerspective([eventIdA], perspectiveName); // Only mark the filtered event type
    });

    // Act - only filter for TestEventA
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)], // Only waiting for TestEventA
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - should complete when eventIdA is processed (eventIdB ignored)
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1)
      .Because("Should only wait for 1 event (TestEventA, not TestEventB)");

    // Verify eventIdB is still tracked (wasn't part of the filtered wait)
    var pendingForB = syncEventTracker.GetPendingEvents(streamId, perspectiveName, [typeof(TestEventB)]);
    await Assert.That(pendingForB.Count).IsEqualTo(1)
      .Because("TestEventB should still be tracked since it wasn't filtered for");
  }

  [Test]
  public async Task WaitForStreamAsync_TrackerFiltersByPerspectiveNameAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventIdMatchingPerspective = Guid.NewGuid();
    var eventIdOtherPerspective = Guid.NewGuid();

    // Track events for different perspectives
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(TestEventA), eventIdMatchingPerspective, streamId, typeof(TestPerspective).FullName!);
    syncEventTracker.TrackEvent(typeof(TestEventA), eventIdOtherPerspective, streamId, "OtherPerspective");

    // With event-driven waiting, coordinator is NOT called when tracker has events
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessedByPerspective only for the matching perspective's event
    // This verifies that filtering by perspective works
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessedByPerspective([eventIdMatchingPerspective], typeof(TestPerspective).FullName!); // Only mark matching perspective's event
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - should complete when matching perspective's event is processed
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1)
      .Because("Should only wait for the event tracked for TestPerspective");

    // Verify other perspective's event is still tracked
    var pendingForOther = syncEventTracker.GetPendingEvents(streamId, "OtherPerspective", [typeof(TestEventA)]);
    await Assert.That(pendingForOther.Count).IsEqualTo(1)
      .Because("OtherPerspective's event should still be tracked");
  }

  [Test]
  public async Task WaitForStreamAsync_WaitsUntilTrackedEventsProcessedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    // With event-driven waiting, coordinator is NOT called when tracker has events
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessedByPerspective after a delay
    // This simulates the real scenario where PerspectiveWorker processes events
    _ = Task.Run(async () => {
      await Task.Delay(100); // Wait a bit to verify we're truly waiting
      syncEventTracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act - should block until MarkProcessed is called
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);
    sw.Stop();

    // Assert - should have waited for event-driven completion
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(80)
      .Because("Should have waited for MarkProcessed to be called");
  }

  [Test]
  public async Task WaitForStreamAsync_WithTrackedEvents_TimesOutCorrectlyAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    // With event-driven waiting, coordinator is NOT called when tracker has events
    // The event is never marked as processed, so it will timeout
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker);

    // Act - No MarkProcessed is called, so event-driven waiting should timeout
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromMilliseconds(200),
        eventIdToAwait: null);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.ElapsedTime.TotalMilliseconds).IsGreaterThanOrEqualTo(150);
  }

  [Test]
  public async Task WaitForStreamAsync_ExplicitEventId_TakesPriorityOverTrackerAsync() {
    // Arrange - explicit eventIdToAwait goes through event-driven wait.
    // Since the explicit ID isn't tracked in SyncEventTracker,
    // WaitForPerspectiveEventsAsync returns true immediately → Synced.
    var streamId = Guid.NewGuid();
    var trackedEventId = Guid.NewGuid();
    var explicitEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Tracker has one event (different from explicitEventId)
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(TestEventA), trackedEventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: syncEventTracker);

    // Act - provide explicit event ID
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: explicitEventId);

    // Assert - explicit event ID goes through event-driven wait, completes immediately
    // since explicitEventId is not tracked in the SyncEventTracker
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);

    // Tracked event should still be in tracker (not cleaned up since we used explicit ID path)
    var pending = syncEventTracker.GetPendingEvents(streamId, perspectiveName, [typeof(TestEventA)]);
    await Assert.That(pending.Count).IsEqualTo(1);
    await Assert.That(pending[0].EventId).IsEqualTo(trackedEventId);
  }

  // ==========================================================================
  // Cross-scope simulation tests
  // ==========================================================================

  [Test]
  public async Task CrossScope_Request1EmitsEvent_Request2WaitsSuccessfullyAsync() {
    // This test simulates the real-world cross-scope scenario:
    // Request 1: Emits StartedEvent (tracked in ISyncEventTracker)
    // Request 2: Has [AwaitPerspectiveSync] and waits for the event

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Shared singleton tracker (simulates production behavior)
    var sharedTracker = new SyncEventTracker();

    // === Request 1: Emit event ===
    // (In production, this would be called by the event emission path)
    sharedTracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    // === Request 2: Wait for sync ===
    // With event-driven waiting, coordinator is NOT called when tracker has events
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    // Request 2 has NO scoped tracker (different scope), but uses shared ISyncEventTracker
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: sharedTracker); // Shared singleton

    // Simulate PerspectiveWorker processing the event
    _ = Task.Run(async () => {
      await Task.Delay(50);
      sharedTracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task CrossScope_SyncComplete_CleansUpTrackerAsync() {
    // This test verifies that when sync completes, the tracked events
    // are removed from the ISyncEventTracker (memory cleanup)
    // NOTE: With event-driven waiting, cleanup happens via MarkProcessed, not the awaiter

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Shared singleton tracker
    var sharedTracker = new SyncEventTracker();
    sharedTracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    // Verify event is tracked before sync
    var pendingBefore = sharedTracker.GetPendingEvents(streamId, perspectiveName);
    await Assert.That(pendingBefore.Count).IsEqualTo(1);

    // With event-driven waiting, coordinator is NOT called when tracker has events
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: sharedTracker);

    // Simulate PerspectiveWorker calling MarkProcessedByPerspective (which both signals waiters AND cleans up)
    _ = Task.Run(async () => {
      await Task.Delay(50);
      sharedTracker.MarkProcessedByPerspective([eventId], perspectiveName); // This removes from tracker
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - sync succeeded
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);

    // Assert - event should be removed from tracker after MarkProcessed was called
    var pendingAfter = sharedTracker.GetPendingEvents(streamId, perspectiveName);
    await Assert.That(pendingAfter.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CrossScope_ExplicitEventId_DoesNotCleanupTrackerAsync() {
    // When explicit eventIdToAwait is provided, the tracker should NOT be cleaned up
    // because we didn't use the tracker as the source of expected IDs.
    // Explicit event ID goes through event-driven wait. Since explicitEventId isn't
    // tracked, WaitForPerspectiveEventsAsync returns true immediately → Synced.

    var streamId = Guid.NewGuid();
    var trackedEventId = Guid.NewGuid();
    var explicitEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Tracker has one event (different from explicitEventId)
    var sharedTracker = new SyncEventTracker();
    sharedTracker.TrackEvent(typeof(TestEventA), trackedEventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: sharedTracker);

    // Act - use explicit event ID
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: explicitEventId);

    // Assert - sync succeeded (explicitEventId not tracked → immediate completion)
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);

    // Assert - tracker should NOT be cleaned up (we used explicit ID, not tracker)
    var pendingAfter = sharedTracker.GetPendingEvents(streamId, perspectiveName);
    await Assert.That(pendingAfter.Count).IsEqualTo(1);
    await Assert.That(pendingAfter[0].EventId).IsEqualTo(trackedEventId);
  }

  [Test]
  public async Task CrossScope_Request1EmitsEvent_Request2WaitsWhilePendingAsync() {
    // This test verifies that Request 2 correctly WAITS when the event
    // exists in the tracker but hasn't been processed yet.

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Shared singleton tracker (simulates production behavior)
    var sharedTracker = new SyncEventTracker();
    sharedTracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    // With event-driven waiting, coordinator is NOT called when tracker has events
    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        syncEventTracker: sharedTracker);

    // Simulate PerspectiveWorker processing the event after a delay
    // This is the CRITICAL cross-scope scenario:
    // - Request 1 tracked the event (above)
    // - PerspectiveWorker processes it (simulated here)
    // - Request 2 (awaiter.WaitForStreamAsync) is notified
    _ = Task.Run(async () => {
      await Task.Delay(100); // Simulate processing time
      sharedTracker.MarkProcessedByPerspective([eventId], perspectiveName); // PerspectiveWorker calls this
    });

    // Act - Request 2 waits for the event to be processed
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);
    sw.Stop();

    // Assert - should have waited and then been notified via event-driven mechanism
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(80)
      .Because("Should have waited for PerspectiveWorker to call MarkProcessed");
  }
}
