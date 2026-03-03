using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// REPRODUCTION TESTS: These tests replicate the user's exact scenario.
///
/// User's scenario:
/// - Request 1: Command A → Receptor A → Returns Event B → Event B should be tracked
/// - Request 2: Command E with [AwaitPerspectiveSync(typeof(C), EventTypes=[typeof(EventB)])]
///              → Should wait for Perspective C to process Event B BEFORE firing
///
/// ACTUAL BUG: Command E's receptor fires BEFORE Perspective C processes Event B
/// </summary>
/// <remarks>
/// These tests use the shared static SyncEventTypeRegistrations, so they must run
/// sequentially to avoid interference.
/// </remarks>
[NotInParallel("SyncTests")]
public class UserScenarioReproductionTests {

  /// <summary>
  /// CRITICAL: Test that demonstrates the complete cross-scope tracking flow.
  ///
  /// This simulates:
  /// 1. Scope 1: Event B is emitted and tracked in singleton tracker
  /// 2. Scope 2: Command E waits for sync
  /// 3. Perspective: Processes Event B and calls MarkProcessed
  /// 4. Result: Command E's await completes AFTER MarkProcessed
  ///
  /// Uses TaskCompletionSource signals for deterministic coordination instead of Task.Delay.
  /// </summary>
  [Test]
  public async Task CrossScope_EventEmittedInScope1_AwaitedInScope2_WaitsForPerspectiveAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();

    var streamId = Guid.NewGuid();
    var eventBId = Guid.NewGuid();
    var perspectiveName = typeof(UserScenarioPerspectiveC).FullName!;

    // Setup registry: EventB → PerspectiveC (simulates module initializer)
    SyncEventTypeRegistrations.Register(typeof(UserScenarioEventB), perspectiveName);

    // Create singleton tracker (shared across scopes)
    var singletonTracker = new SyncEventTracker();

    // Create registry that reads from SyncEventTypeRegistrations
    var typeRegistry = new TrackedEventTypeRegistry();

    // Synchronization signals - proper coordination instead of Task.Delay
    var syncWaitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var markProcessedCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var syncCompleted = new TaskCompletionSource<SyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);

    // Track timing for verification (timestamps, not execution order counters)
    var markProcessedTime = DateTime.MinValue;
    var syncCompletedTime = DateTime.MinValue;

    // === SCOPE 1: Command A emits Event B ===
    // This simulates what happens in Dispatcher._cascadeEventsFromResultAsync
    var perspectiveNames = typeRegistry.GetPerspectiveNames(typeof(UserScenarioEventB));

    // CRITICAL ASSERTION: Registry MUST return the perspective name
    await Assert.That(perspectiveNames.Count).IsGreaterThan(0)
      .Because("Registry MUST have mapping for EventB → PerspectiveC. " +
               "If this fails, the module initializer (source generator) isn't working.");

    // Track the event (simulates Dispatcher line 1889)
    foreach (var name in perspectiveNames) {
      singletonTracker.TrackEvent(typeof(UserScenarioEventB), eventBId, streamId, name);
    }

    // Verify event is now in tracker
    var trackedEvents = singletonTracker.GetPendingEvents(streamId, perspectiveName, [typeof(UserScenarioEventB)]);
    await Assert.That(trackedEvents.Count).IsEqualTo(1)
      .Because("Event B MUST be in singleton tracker after emit");
    await Assert.That(trackedEvents[0].EventId).IsEqualTo(eventBId)
      .Because("Tracked eventId MUST match what we tracked");

    // === SCOPE 2: Command E waits for sync ===
    var syncTask = Task.Run(async () => {
      // Create mock coordinator that checks singleton tracker
      var mockCoordinator = new MockWorkCoordinatorWithTracker(singletonTracker, perspectiveName);

      var awaiter = new PerspectiveSyncAwaiter(
        mockCoordinator,
        new DebuggerAwareClock(new() { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker: null,
        syncEventTracker: singletonTracker);

      // Signal that sync is about to start waiting
      syncWaitingStarted.SetResult();

      var result = await awaiter.WaitForStreamAsync(
        typeof(UserScenarioPerspectiveC),
        streamId,
        eventTypes: [typeof(UserScenarioEventB)],
        timeout: TimeSpan.FromSeconds(5));

      syncCompletedTime = DateTime.UtcNow;
      syncCompleted.SetResult(result);
      return result;
    });

    // === PERSPECTIVE: Process Event B and call MarkProcessed ===
    var perspectiveTask = Task.Run(async () => {
      // Wait until sync task has started waiting before processing
      await syncWaitingStarted.Task;

      // Small yield to ensure awaiter has started its polling loop
      await Task.Yield();

      // Simulate what PerspectiveWorker does - call MarkProcessedByPerspective with the event ID
      // CRITICAL: This must use the SAME eventId that was tracked
      singletonTracker.MarkProcessedByPerspective([eventBId], typeof(UserScenarioPerspectiveC).FullName!);
      markProcessedTime = DateTime.UtcNow;
      markProcessedCompleted.SetResult();
    });

    // Wait for both tasks
    await Task.WhenAll(syncTask, perspectiveTask);
    var syncResult = await syncCompleted.Task;

    // === ASSERTIONS ===

    // 1. Sync should succeed (not timeout)
    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Sync MUST succeed after perspective processes event and calls MarkProcessed");

    // 2. Verify timing: MarkProcessed completed BEFORE or at the same time as sync completed
    // This is the key invariant - the awaiter detects the MarkProcessed and then completes
    await Assert.That(markProcessedTime).IsLessThanOrEqualTo(syncCompletedTime)
      .Because("Perspective MUST call MarkProcessed BEFORE sync can complete");

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// Test that verifies timeout behavior when perspective doesn't process in time.
  /// </summary>
  [Test]
  public async Task CrossScope_PerspectiveNeverProcesses_SyncTimesOutAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();

    var streamId = Guid.NewGuid();
    var eventBId = Guid.NewGuid();
    var perspectiveName = typeof(UserScenarioPerspectiveC).FullName!;

    // Setup registry
    SyncEventTypeRegistrations.Register(typeof(UserScenarioEventB), perspectiveName);

    var singletonTracker = new SyncEventTracker();
    var typeRegistry = new TrackedEventTypeRegistry();

    // Track event
    var perspectiveNames = typeRegistry.GetPerspectiveNames(typeof(UserScenarioEventB));
    foreach (var name in perspectiveNames) {
      singletonTracker.TrackEvent(typeof(UserScenarioEventB), eventBId, streamId, name);
    }

    // Create mock coordinator that always returns pending
    var mockCoordinator = MockWorkCoordinator.WithSyncResults(pendingCount: 1);

    var awaiter = new PerspectiveSyncAwaiter(
      mockCoordinator,
      new DebuggerAwareClock(new() { Mode = DebuggerDetectionMode.Disabled }),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      tracker: null,
      syncEventTracker: singletonTracker);

    // Act - wait with short timeout (perspective never calls MarkProcessed)
    var result = await awaiter.WaitForStreamAsync(
      typeof(UserScenarioPerspectiveC),
      streamId,
      eventTypes: [typeof(UserScenarioEventB)],
      timeout: TimeSpan.FromMilliseconds(100));

    // Assert - should timeout
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut)
      .Because("Sync should timeout when perspective never processes the event");

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// Test that verifies immediate sync when no events are pending.
  /// </summary>
  [Test]
  public async Task CrossScope_NoEventsPending_SyncsImmediatelyAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();

    var streamId = Guid.NewGuid();
    var perspectiveName = typeof(UserScenarioPerspectiveC).FullName!;

    // Setup registry but DON'T track any events
    SyncEventTypeRegistrations.Register(typeof(UserScenarioEventB), perspectiveName);

    var singletonTracker = new SyncEventTracker();

    // Create mock coordinator
    var mockCoordinator = MockWorkCoordinator.WithSyncResults(pendingCount: 0);

    var awaiter = new PerspectiveSyncAwaiter(
      mockCoordinator,
      new DebuggerAwareClock(new() { Mode = DebuggerDetectionMode.Disabled }),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      tracker: null,
      syncEventTracker: singletonTracker);

    // Act - no events tracked, should sync immediately
    var result = await awaiter.WaitForStreamAsync(
      typeof(UserScenarioPerspectiveC),
      streamId,
      eventTypes: [typeof(UserScenarioEventB)],
      timeout: TimeSpan.FromSeconds(5));

    // Assert - should sync immediately (no pending events)
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Sync should complete immediately when no events are pending");

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }
}

// Test types for user scenario
internal sealed class UserScenarioEventB { }

internal sealed class UserScenarioPerspectiveC { }

/// <summary>
/// Mock work coordinator that integrates with the singleton tracker.
/// Returns synced when all tracked events have been marked as processed.
/// </summary>
internal sealed class MockWorkCoordinatorWithTracker : IWorkCoordinator {
  private readonly ISyncEventTracker _tracker;
  private readonly string _perspectiveName;

  public MockWorkCoordinatorWithTracker(ISyncEventTracker tracker, string perspectiveName) {
    _tracker = tracker;
    _perspectiveName = perspectiveName;
  }

  public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) {
    // Check if there are any pending events for any of the sync inquiries
    var results = new List<SyncInquiryResult>();

    if (request.PerspectiveSyncInquiries is { Length: > 0 }) {
      foreach (var inquiry in request.PerspectiveSyncInquiries) {
        var pendingEvents = _tracker.GetPendingEvents(
          inquiry.StreamId,
          inquiry.PerspectiveName ?? _perspectiveName,
          null); // EventTypes filtering done by tracker

        // If no pending events, we're synced
        var pendingCount = pendingEvents.Count;

        results.Add(new SyncInquiryResult {
          InquiryId = inquiry.InquiryId,
          StreamId = inquiry.StreamId,
          PendingCount = pendingCount,
          ProcessedCount = pendingCount == 0 ? 1 : 0,
          ProcessedEventIds = pendingCount == 0 ? inquiry.EventIds : []
        });
      }
    }

    return Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = results
    });
  }

  public Task ReportPerspectiveCompletionAsync(PerspectiveCheckpointCompletion completion, CancellationToken ct = default) {
    return Task.CompletedTask;
  }

  public Task ReportPerspectiveFailureAsync(PerspectiveCheckpointFailure failure, CancellationToken ct = default) {
    return Task.CompletedTask;
  }

  public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
    return Task.FromResult<PerspectiveCheckpointInfo?>(null);
  }
}
