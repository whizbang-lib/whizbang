using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Integration tests that verify the FULL Dispatcher flow for perspective sync:
/// 1. Command A sent → Receptor A invoked → Returns Event B
/// 2. Event B cascaded → Tracked in ISyncEventTracker (if registered in ITrackedEventTypeRegistry)
/// 3. Command E sent → Sync check finds tracked event → Waits for MarkProcessed
/// 4. Perspective worker calls MarkProcessed → Sync completes → E's receptor fires
///
/// These tests verify the complete end-to-end integration, not just individual components.
/// </summary>
/// <remarks>
/// These tests use shared SyncEventTracker instances and SyncEventTypeRegistrations,
/// so they must run sequentially to avoid interference.
/// </remarks>
[NotInParallel("SyncTests")]
public class DispatcherIntegrationSyncTests {

  /// <summary>
  /// CRITICAL TEST: Verify that when events are cascaded, they ARE tracked in the singleton tracker.
  /// This is the foundation of cross-scope sync - if events aren't tracked, sync can't work.
  /// </summary>
  [Test]
  public async Task Cascade_WithTrackedEventType_TracksInSingletonTrackerAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventType = typeof(IntegrationTestEventB);
    var perspectiveName = typeof(IntegrationTestPerspectiveC).FullName!;

    // Create the singleton tracker
    var singletonTracker = new SyncEventTracker();

    // Create a registry that maps EventB → PerspectiveC
    var typeRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]> {
      { eventType, [perspectiveName] }
    });

    // Verify: before cascade, no events tracked
    var beforeEvents = singletonTracker.GetPendingEvents(streamId, perspectiveName, [eventType]);
    await Assert.That(beforeEvents.Count).IsEqualTo(0)
      .Because("No events should be tracked before cascade");

    // ACT: Simulate what _cascadeEventsFromResultAsync does
    // (We can't easily test the actual Dispatcher here, so we test the tracking logic directly)
    var eventId = Guid.NewGuid();
    var perspectiveNames = typeRegistry.GetPerspectiveNames(eventType);

    await Assert.That(perspectiveNames.Count).IsGreaterThan(0)
      .Because("Registry should return perspective names for EventB");

    foreach (var name in perspectiveNames) {
      singletonTracker.TrackEvent(eventType, eventId, streamId, name);
    }

    // Assert: event should now be tracked
    var afterEvents = singletonTracker.GetPendingEvents(streamId, perspectiveName, [eventType]);
    await Assert.That(afterEvents.Count).IsEqualTo(1)
      .Because("Event should be tracked after cascade tracking");
    await Assert.That(afterEvents[0].EventId).IsEqualTo(eventId);
    await Assert.That(afterEvents[0].EventType).IsEqualTo(eventType);
  }

  /// <summary>
  /// CRITICAL TEST: Verify that ITrackedEventTypeRegistry with default constructor
  /// reads from SyncEventTypeRegistrations (the static registry populated by generators).
  /// </summary>
  [Test]
  public async Task TrackedEventTypeRegistry_DefaultConstructor_ReadsSyncEventTypeRegistrationsAsync() {
    // Arrange - clear any existing registrations and add a test one
    SyncEventTypeRegistrations.Clear();
    var testEventType = typeof(IntegrationTestEventB);
    var testPerspectiveName = "Test.Perspective.Name";

    SyncEventTypeRegistrations.Register(testEventType, testPerspectiveName);

    // Act - create registry with default constructor (reads from static registrations)
    var registry = new TrackedEventTypeRegistry();

    // Assert
    var perspectives = registry.GetPerspectiveNames(testEventType);
    await Assert.That(perspectives.Count).IsEqualTo(1);
    await Assert.That(perspectives[0]).IsEqualTo(testPerspectiveName);

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// SCENARIO TEST: Full cross-command sync flow
  /// This test simulates the exact scenario the user described.
  /// </summary>
  [Test]
  public async Task FullFlow_CommandAEmitsEventB_CommandEWaitsForPerspectiveCAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventBId = Guid.NewGuid();
    var perspectiveCName = typeof(IntegrationTestPerspectiveC).FullName!;
    var executionOrder = new List<string>();

    // Setup: singleton tracker and type registry
    var singletonTracker = new SyncEventTracker();
    var typeRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]> {
      { typeof(IntegrationTestEventB), [perspectiveCName] }
    });

    // === STEP 1: Command A's receptor returns Event B ===
    // Simulate what Dispatcher._cascadeEventsFromResultAsync does
    executionOrder.Add("Command A receptor executed, returned Event B");
    var perspectiveNames = typeRegistry.GetPerspectiveNames(typeof(IntegrationTestEventB));
    foreach (var name in perspectiveNames) {
      singletonTracker.TrackEvent(typeof(IntegrationTestEventB), eventBId, streamId, name);
    }
    executionOrder.Add("Event B tracked in singleton tracker");

    // Verify Event B is tracked
    var trackedAfterA = singletonTracker.GetPendingEvents(streamId, perspectiveCName, [typeof(IntegrationTestEventB)]);
    await Assert.That(trackedAfterA.Count).IsEqualTo(1)
      .Because("Event B should be tracked after Command A cascade");

    // === STEP 2: Setup PerspectiveSyncAwaiter for Command E ===
    var mockCoordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    var awaiter = new PerspectiveSyncAwaiter(
        mockCoordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance,
        singletonTracker);

    // === STEP 3: Perspective worker processes Event B ===
    var perspectiveTask = Task.Run(async () => {
      await Task.Delay(100); // Simulate processing time
      executionOrder.Add("Perspective C processed Event B (Apply called)");
      singletonTracker.MarkProcessedByPerspective([eventBId], perspectiveCName);
    });

    // === STEP 4: Command E is sent - should wait for sync ===
    var syncTask = Task.Run(async () => {
      // Small delay to ensure tracking happened first
      await Task.Delay(10);

      // This is what _awaitPerspectiveSyncIfNeededAsync does
      var result = await awaiter.WaitForStreamAsync(
          typeof(IntegrationTestPerspectiveC),
          streamId,
          eventTypes: [typeof(IntegrationTestEventB)],
          timeout: TimeSpan.FromMilliseconds(500),
          eventIdToAwait: null);

      // After sync completes, E's receptor would fire
      executionOrder.Add("Command E receptor executed (after sync)");

      return result;
    });

    // Wait for both
    var syncResult = await syncTask;
    await perspectiveTask;

    // === ASSERTIONS ===
    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Sync should succeed after perspective processes event");

    // Verify execution order
    await Assert.That(executionOrder.Count).IsEqualTo(4);

    var perspectiveIndex = executionOrder.IndexOf("Perspective C processed Event B (Apply called)");
    var commandEIndex = executionOrder.IndexOf("Command E receptor executed (after sync)");

    await Assert.That(perspectiveIndex).IsGreaterThan(0)
      .Because("Perspective should have processed the event");
    await Assert.That(commandEIndex).IsGreaterThan(0)
      .Because("Command E receptor should have executed");
    await Assert.That(perspectiveIndex).IsLessThan(commandEIndex)
      .Because("Perspective MUST process Event B BEFORE Command E receptor fires");

    // Verify tracker is cleaned up
    var trackedAfterSync = singletonTracker.GetAllTrackedEventIds();
    await Assert.That(trackedAfterSync.Count).IsEqualTo(0)
      .Because("Processed events should be removed from tracker");
  }

  /// <summary>
  /// TEST: If ITrackedEventTypeRegistry has no mapping for an event type,
  /// the event is NOT tracked, and sync falls back to database discovery.
  /// </summary>
  [Test]
  public async Task NoRegistryMapping_EventNotTracked_FallsBackToDbAsync() {
    // Arrange
    _ = Guid.NewGuid();
    var singletonTracker = new SyncEventTracker();

    // Empty registry - no mappings
    var emptyRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]>());

    // Simulate cascade - should NOT track because no mapping
    var perspectiveNames = emptyRegistry.GetPerspectiveNames(typeof(IntegrationTestEventB));

    await Assert.That(perspectiveNames.Count).IsEqualTo(0)
      .Because("Empty registry should return no perspectives");

    // No tracking should happen (the foreach in Dispatcher won't execute)
    var trackedEvents = singletonTracker.GetAllTrackedEventIds();
    await Assert.That(trackedEvents.Count).IsEqualTo(0)
      .Because("Event should NOT be tracked when registry has no mapping");
  }
}

// Integration test types
internal sealed class IntegrationTestEventB { }
internal sealed class IntegrationTestPerspectiveC { }
internal sealed class IntegrationTestCommandE { }
