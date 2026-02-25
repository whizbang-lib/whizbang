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
        tracker: scopedTracker,
        syncEventTracker: syncEventTracker);

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
        tracker: null,
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
        tracker: null,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessed after a short delay
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessed([eventId]);
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
  public async Task WaitForStreamAsync_WithNoTrackedEvents_FallsThroughToDbDiscoveryAsync() {
    // Arrange - tracker is empty (no events tracked for this stream)
    // This is the CRITICAL scenario: ISyncEventTracker exists but has no events
    // We should NOT return Synced immediately - fall through to database discovery
    var streamId = Guid.NewGuid();
    var syncEventTracker = new SyncEventTracker();
    // Track an event on a DIFFERENT stream (so our target stream has no tracked events)
    syncEventTracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), Guid.NewGuid(), typeof(TestPerspective).FullName!);

    SyncInquiry? capturedInquiry = null;
    var coordinator = new MockWorkCoordinator((request, _) => {
      capturedInquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = capturedInquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,
            ProcessedCount = 0,
            ProcessedEventIds = []
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker: null,
        syncEventTracker: syncEventTracker);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - should fall through to database discovery
    await Assert.That(capturedInquiry).IsNotNull()
      .Because("Should query database when tracker has no events for this stream");
    await Assert.That(capturedInquiry!.DiscoverPendingFromOutbox).IsTrue()
      .Because("Should use database discovery when no explicit event IDs available");
    await Assert.That(capturedInquiry.EventTypeFilter).IsNotNull()
      .Because("EventTypeFilter should be set for database discovery");
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
        tracker: null,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessed for eventIdA only
    // This verifies that filtering works - only eventIdA is waited for (not eventIdB)
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessed([eventIdA]); // Only mark the filtered event type
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
        tracker: null,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessed only for the matching perspective's event
    // This verifies that filtering by perspective works
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessed([eventIdMatchingPerspective]); // Only mark matching perspective's event
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
        tracker: null,
        syncEventTracker: syncEventTracker);

    // Simulate perspective worker calling MarkProcessed after a delay
    // This simulates the real scenario where PerspectiveWorker processes events
    _ = Task.Run(async () => {
      await Task.Delay(100); // Wait a bit to verify we're truly waiting
      syncEventTracker.MarkProcessed([eventId]);
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
        tracker: null,
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
    // Arrange
    var streamId = Guid.NewGuid();
    var trackedEventId = Guid.NewGuid();
    var explicitEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Tracker has one event
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(TestEventA), trackedEventId, streamId, perspectiveName);

    SyncInquiry? capturedInquiry = null;
    var coordinator = new MockWorkCoordinator((request, _) => {
      capturedInquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = capturedInquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,
            ProcessedCount = 1,
            ProcessedEventIds = [explicitEventId]
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker: null,
        syncEventTracker: syncEventTracker);

    // Act - provide explicit event ID
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: explicitEventId);

    // Assert - explicit event ID takes priority over tracker
    await Assert.That(capturedInquiry).IsNotNull();
    await Assert.That(capturedInquiry!.EventIds).IsNotNull();
    await Assert.That(capturedInquiry.EventIds!.Length).IsEqualTo(1);
    await Assert.That(capturedInquiry.EventIds).Contains(explicitEventId);
    await Assert.That(capturedInquiry.EventIds).DoesNotContain(trackedEventId);
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
        tracker: null, // No scoped tracker - cross-scope
        syncEventTracker: sharedTracker); // Shared singleton

    // Simulate PerspectiveWorker processing the event
    _ = Task.Run(async () => {
      await Task.Delay(50);
      sharedTracker.MarkProcessed([eventId]);
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
        tracker: null,
        syncEventTracker: sharedTracker);

    // Simulate PerspectiveWorker calling MarkProcessed (which both signals waiters AND cleans up)
    _ = Task.Run(async () => {
      await Task.Delay(50);
      sharedTracker.MarkProcessed([eventId]); // This removes from tracker
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
    // because we didn't use the tracker as the source of expected IDs

    var streamId = Guid.NewGuid();
    var trackedEventId = Guid.NewGuid();
    var explicitEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Tracker has one event (different from explicitEventId)
    var sharedTracker = new SyncEventTracker();
    sharedTracker.TrackEvent(typeof(TestEventA), trackedEventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator((request, _) => {
      var inquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = inquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,
            ProcessedCount = 1,
            ProcessedEventIds = [explicitEventId]
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker: null,
        syncEventTracker: sharedTracker);

    // Act - use explicit event ID
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: explicitEventId);

    // Assert - sync succeeded
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);

    // Assert - tracker should NOT be cleaned up (we used explicit ID, not tracker)
    var pendingAfter = sharedTracker.GetPendingEvents(streamId, perspectiveName);
    await Assert.That(pendingAfter.Count).IsEqualTo(1);
    await Assert.That(pendingAfter[0].EventId).IsEqualTo(trackedEventId);
  }

  /// <summary>
  /// CRITICAL BUG FIX TEST:
  /// This test reproduces the EXACT user scenario where:
  /// 1. Request 1 (StartActivityCommandHandler) emits StartedEvent
  /// 2. Request 2 (RequestActivityStatusCommandHandler) has [AwaitPerspectiveSync]
  /// 3. The ISyncEventTracker exists but the event was NOT tracked (e.g., generator not wired)
  /// 4. BUG: Handler fired immediately because tracker had no events -> returned Synced
  /// 5. FIX: Should fall through to database discovery and wait for pending events
  /// </summary>
  [Test]
  public async Task BUGFIX_TrackerHasNoEvents_ShouldFallThroughToDbDiscovery_AndWaitAsync() {
    // Arrange - This simulates the user's exact scenario:
    // - ISyncEventTracker exists (it's registered in DI)
    // - BUT the event was NOT tracked (ITrackedEventTypeRegistry not populated by generator)
    // - Event EXISTS in database (Request 1 stored it) but perspective hasn't processed it
    var streamId = Guid.NewGuid();
    var pendingEventId = Guid.NewGuid();

    // Empty tracker - event was NOT tracked (simulates generator not wiring correctly)
    var emptyTracker = new SyncEventTracker();

    var queryCount = 0;
    var coordinator = new MockWorkCoordinator((request, _) => {
      queryCount++;
      var inquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();

      // Simulate: Event IS in database, IS pending (not yet processed by perspective)
      // On 4th query, event is processed
      var isProcessed = queryCount >= 4;

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = inquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = isProcessed ? 0 : 1,
            ProcessedCount = isProcessed ? 1 : 0,
            ProcessedEventIds = isProcessed ? [pendingEventId] : [],
            PendingEventIds = isProcessed ? [] : [pendingEventId]
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    // Request 2 has:
    // - No scoped tracker (different scope from Request 1)
    // - ISyncEventTracker exists but is EMPTY for this stream
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker: null,
        syncEventTracker: emptyTracker);

    // Act - This is what [AwaitPerspectiveSync] does
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert
    // BEFORE FIX: Would return Synced immediately (queryCount = 0) - BUG!
    // AFTER FIX: Falls through to database discovery, waits for event
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Should wait for database to confirm event is processed");
    await Assert.That(queryCount).IsGreaterThanOrEqualTo(4)
      .Because("Should poll database until event is processed (not return immediately)");
  }

  /// <summary>
  /// Verifies that when tracker is empty, the inquiry has DiscoverPendingFromOutbox = true.
  /// </summary>
  [Test]
  public async Task BUGFIX_TrackerEmpty_ShouldSetDiscoverPendingFromOutboxAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var emptyTracker = new SyncEventTracker();

    SyncInquiry? capturedInquiry = null;
    var coordinator = new MockWorkCoordinator((request, _) => {
      capturedInquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = capturedInquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,
            ProcessedCount = 1
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(
        coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker: null,
        syncEventTracker: emptyTracker);

    // Act
    await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(TestEventA)],
        timeout: TimeSpan.FromSeconds(1),
        eventIdToAwait: null);

    // Assert - inquiry should use database discovery
    await Assert.That(capturedInquiry).IsNotNull();
    await Assert.That(capturedInquiry!.DiscoverPendingFromOutbox).IsTrue()
      .Because("When tracker is empty, should fall through to database discovery");
    await Assert.That(capturedInquiry.EventIds).IsNull()
      .Because("No explicit event IDs when using database discovery");
    await Assert.That(capturedInquiry.EventTypeFilter).IsNotNull()
      .Because("Should include event type filter for discovery");
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
        tracker: null,
        syncEventTracker: sharedTracker);

    // Simulate PerspectiveWorker processing the event after a delay
    // This is the CRITICAL cross-scope scenario:
    // - Request 1 tracked the event (above)
    // - PerspectiveWorker processes it (simulated here)
    // - Request 2 (awaiter.WaitForStreamAsync) is notified
    _ = Task.Run(async () => {
      await Task.Delay(100); // Simulate processing time
      sharedTracker.MarkProcessed([eventId]); // PerspectiveWorker calls this
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
