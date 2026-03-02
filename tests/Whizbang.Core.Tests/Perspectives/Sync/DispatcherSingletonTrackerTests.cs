using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests that verify the singleton ISyncEventTracker and ITrackedEventTypeRegistry
/// work together for cross-scope perspective sync.
///
/// The scenario:
/// 1. Request 1: Handler returns Route.Local(event) - event gets tracked
/// 2. Singleton tracker persists across requests
/// 3. Request 2: Different handler with [AwaitPerspectiveSync]
/// 4. WaitForStreamAsync checks singleton tracker and finds the event
/// 5. Handler waits until event is processed
/// </summary>
/// <remarks>
/// These tests use shared SyncEventTracker instances, so they must run
/// sequentially to avoid interference.
/// </remarks>
[NotInParallel("SyncTests")]
public class DispatcherSingletonTrackerTests {

  /// <summary>
  /// CORE TEST: Verify that the singleton tracker correctly bridges request scopes.
  /// This simulates what happens when events are tracked in one request
  /// and awaited in another.
  /// </summary>
  [Test]
  public async Task SingletonTracker_EventTrackedInRequest1_FoundByRequest2Async() {
    // Arrange - Shared singleton tracker (simulates DI singleton)
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = "MyApp.Perspectives.ActivityProjection";
    var eventType = typeof(SingletonTestStartedEvent);

    var singletonTracker = new SyncEventTracker();

    // === REQUEST 1: Track an event ===
    singletonTracker.TrackEvent(eventType, eventId, streamId, perspectiveName);

    // === REQUEST 2: Different scope, same singleton ===
    var pendingEvents = singletonTracker.GetPendingEvents(streamId, perspectiveName, [eventType]);

    // Assert - Request 2 should see the event tracked by Request 1
    await Assert.That(pendingEvents.Count).IsEqualTo(1)
      .Because("Events tracked in Request 1 should be visible in Request 2");

    await Assert.That(pendingEvents[0].EventId).IsEqualTo(eventId);
    await Assert.That(pendingEvents[0].StreamId).IsEqualTo(streamId);
    await Assert.That(pendingEvents[0].EventType).IsEqualTo(eventType);
    await Assert.That(pendingEvents[0].PerspectiveName).IsEqualTo(perspectiveName);
  }

  /// <summary>
  /// Verify that PerspectiveSyncAwaiter correctly uses the singleton tracker.
  /// With event-driven waiting, it waits for MarkProcessed to be called.
  /// </summary>
  [Test]
  public async Task PerspectiveSyncAwaiter_WithSingletonTracker_UsesTrackedEventsAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(SingletonTestProjection).FullName!;
    var eventType = typeof(SingletonTestStartedEvent);

    // Create singleton tracker with a pre-tracked event
    var singletonTracker = new SyncEventTracker();
    singletonTracker.TrackEvent(eventType, eventId, streamId, perspectiveName);

    // With event-driven waiting, the coordinator is NOT called when using singleton tracker
    // Instead, the awaiter waits for MarkProcessed to be called on the tracker
    var mockCoordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    // Create awaiter WITH singleton tracker
    var awaiter = new PerspectiveSyncAwaiter(
      mockCoordinator,
      clock,
      logger,
      tracker: null,  // No scoped tracker
      syncEventTracker: singletonTracker  // <-- KEY: Uses singleton tracker
    );

    // Act - wait for event (will timeout since MarkProcessed is not called)
    var result = await awaiter.WaitForStreamAsync(
      typeof(SingletonTestProjection),
      streamId,
      [eventType],
      timeout: TimeSpan.FromMilliseconds(200)
    );

    // Assert - Should timeout because MarkProcessed was never called
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut)
      .Because("Event is pending - MarkProcessed was not called");

    // Verify the event is still being tracked
    var pendingEvents = singletonTracker.GetPendingEvents(streamId, perspectiveName, [eventType]);
    await Assert.That(pendingEvents.Count).IsEqualTo(1)
      .Because("Event should still be tracked since it was not processed");
    await Assert.That(pendingEvents[0].EventId).IsEqualTo(eventId);
  }

  /// <summary>
  /// Verify that when sync completes (via MarkProcessed), events are removed from tracker.
  /// With event-driven waiting, sync completes when MarkProcessed is called on the tracker.
  /// </summary>
  [Test]
  public async Task PerspectiveSyncAwaiter_AfterSyncCompletes_CleansUpTrackerAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(SingletonTestProjection).FullName!;

    var singletonTracker = new SyncEventTracker();
    singletonTracker.TrackEvent(typeof(SingletonTestStartedEvent), eventId, streamId, perspectiveName);

    // Verify it's tracked
    var before = singletonTracker.GetAllTrackedEventIds();
    await Assert.That(before.Count).IsEqualTo(1);

    // With event-driven waiting, coordinator is not called - waiting is done via tracker
    var mockCoordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    var awaiter = new PerspectiveSyncAwaiter(
      mockCoordinator,
      clock,
      logger,
      tracker: null,
      syncEventTracker: singletonTracker
    );

    // Simulate perspective worker calling MarkProcessedByPerspective after a delay
    _ = Task.Run(async () => {
      await Task.Delay(50);
      singletonTracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
      typeof(SingletonTestProjection),
      streamId,
      [typeof(SingletonTestStartedEvent)],
      timeout: TimeSpan.FromSeconds(5)
    );

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);

    var after = singletonTracker.GetAllTrackedEventIds();
    await Assert.That(after.Count).IsEqualTo(0)
      .Because("Processed events should be removed from singleton tracker");
  }

  /// <summary>
  /// CRITICAL TEST: Without singleton tracker, awaiter should still work
  /// by falling through to database discovery.
  /// </summary>
  [Test]
  public async Task PerspectiveSyncAwaiter_WithoutSingletonTracker_FallsThroughToDbDiscoveryAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    SyncInquiry? capturedInquiry = null;

    var mockCoordinator = new MockWorkCoordinator((request, _) => {
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
            ProcessedCount = 0
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock();
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    // Create awaiter WITHOUT singleton tracker (simulates missing DI registration)
    var awaiter = new PerspectiveSyncAwaiter(
      mockCoordinator,
      clock,
      logger,
      tracker: null,
      syncEventTracker: null  // <-- No singleton tracker
    );

    // Act
    await awaiter.WaitForStreamAsync(
      typeof(SingletonTestProjection),
      streamId,
      [typeof(SingletonTestStartedEvent)],
      timeout: TimeSpan.FromMilliseconds(100)
    );

    // Assert - Should fall through to database discovery
    await Assert.That(capturedInquiry).IsNotNull();
    await Assert.That(capturedInquiry!.DiscoverPendingFromOutbox).IsTrue()
      .Because("Without singleton tracker, should discover from database");
    await Assert.That(capturedInquiry.EventIds).IsNull()
      .Because("No explicit event IDs - using discovery mode");
  }

  /// <summary>
  /// Verify TrackedEventTypeRegistry correctly determines which perspectives
  /// to track for a given event type.
  /// </summary>
  [Test]
  public async Task TrackedEventTypeRegistry_GetPerspectiveNames_ReturnsCorrectPerspectivesAsync() {
    // Arrange
    var perspective1 = "MyApp.Perspectives.ActivityProjection";
    var perspective2 = "MyApp.Perspectives.ReportingProjection";

    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]> {
      { typeof(SingletonTestStartedEvent), [perspective1, perspective2] },  // Same event, two perspectives
      { typeof(SingletonTestCompletedEvent), [perspective1] }
    });

    // Act & Assert
    var perspectivesForStarted = registry.GetPerspectiveNames(typeof(SingletonTestStartedEvent));
    await Assert.That(perspectivesForStarted).Contains(perspective1);
    await Assert.That(perspectivesForStarted).Contains(perspective2);

    var perspectivesForCompleted = registry.GetPerspectiveNames(typeof(SingletonTestCompletedEvent));
    await Assert.That(perspectivesForCompleted).Contains(perspective1);
    await Assert.That(perspectivesForCompleted.Count).IsEqualTo(1);

    var perspectivesForUnregistered = registry.GetPerspectiveNames(typeof(string));
    await Assert.That(perspectivesForUnregistered.Count).IsEqualTo(0);
  }
}

// Test types (prefixed to avoid collision with other test files)
internal sealed class SingletonTestProjection { }
internal sealed class SingletonTestStartedEvent { }
internal sealed class SingletonTestCompletedEvent { }
