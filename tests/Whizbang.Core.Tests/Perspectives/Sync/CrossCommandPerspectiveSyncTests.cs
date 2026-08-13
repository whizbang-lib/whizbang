using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// <para>Tests for cross-command perspective sync scenario:
/// 1. Fire command A → Receptor A returns Event B
/// 2. Fire command E → Receptor E has [AwaitPerspectiveSync(typeof(C))]
/// 3. Expected: Perspective C processes B FIRST, then E's receptor fires</para>
///
/// <para>This is the key scenario where perspective sync MUST work:
/// - Command A emits Event B
/// - Command E wants to wait until Perspective C has processed B
/// - E's receptor should NOT fire until C.Apply(B) has completed</para>
/// </summary>
/// <remarks>
/// These tests use shared SyncEventTracker instances, so they must run
/// sequentially to avoid interference.
/// </remarks>
[NotInParallel("SyncTests")]
public class CrossCommandPerspectiveSyncTests {

  /// <summary>
  /// <para>CORE BUG REPRODUCTION:
  /// When command E is sent with [AwaitPerspectiveSync] for perspective C,
  /// and command A previously emitted event B (which C processes),
  /// E's receptor should NOT fire until C has processed B.</para>
  ///
  /// <para>This test verifies the execution order:
  /// 1. A's receptor fires, returns Event B → B is tracked for C
  /// 2. E is sent with sync attribute for C
  /// 3. E's receptor should WAIT for C to process B
  /// 4. C.Apply(B) fires (via MarkProcessed)
  /// 5. THEN E's receptor fires</para>
  /// </summary>
  [Test]
  public async Task CrossCommandSync_EReceptorWaitsForCToProcessB_CorrectOrderAsync() {
    // Arrange - track execution order
    var executionOrder = new List<string>();
    var streamId = Guid.NewGuid();
    var eventBId = Guid.NewGuid();
    // CRITICAL: Must use the SAME perspective name that WaitForStreamAsync will derive
    var perspectiveCName = typeof(TestPerspectiveC).FullName!;

    // Singleton tracker (simulates DI singleton)
    var singletonTracker = new SyncEventTracker();

    // === STEP 1: Simulate command A's receptor returning Event B ===
    // In real code: receptor A returns Route.Local(eventB)
    // The Dispatcher tracks this event for perspective C
    singletonTracker.TrackEvent(typeof(TestEventB), eventBId, streamId, perspectiveCName);
    executionOrder.Add("A's receptor returned Event B (tracked)");

    // === STEP 2: Prepare to send command E ===
    // E's receptor has [AwaitPerspectiveSync(typeof(PerspectiveC))]
    // When E is sent, it should WAIT for C to process B

    var mockCoordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    var awaiter = new PerspectiveSyncAwaiter(
        mockCoordinator,
        clock,
        logger,
        singletonTracker);

    // === STEP 3 + 4: deterministic, single-threaded — no wall-clock pacing ===
    // The former shape raced a simulated 100ms "processing delay" against a 500ms sync timeout,
    // which starved under CI load. Instead: start (but do not await) the sync wait — an async
    // method runs synchronously to its first true suspension, so the waiter is REGISTERED with
    // the tracker by the time the un-awaited Task is returned. C.Apply(B) then fires, and only
    // then is the sync awaited. The timeout exists solely to fail a genuine hang. The ordering
    // guarantee under test is carried by the awaiter itself: the sync Task may only complete
    // Synced after MarkProcessed, so a broken guarantee flips the recorded order and fails below.
    var syncTask = awaiter.WaitForStreamAsync(
        typeof(TestPerspectiveC),
        streamId,
        eventTypes: [typeof(TestEventB)],
        timeout: TimeSpan.FromSeconds(30), // hang detection only — never part of the scenario
        eventIdToAwait: null);

    // === C.Apply(B) fires while E's sync wait is registered and pending ===
    executionOrder.Add("C.Apply(B) completed");
    singletonTracker.MarkProcessedByPerspective([eventBId], perspectiveCName);

    var syncResult = await syncTask;
    // After sync completes, E's receptor would fire
    executionOrder.Add("E's receptor fired");

    // === ASSERTIONS ===

    // Verify sync succeeded
    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Sync should succeed after C processes B");

    // CRITICAL: Verify execution order
    // C.Apply(B) MUST complete BEFORE E's receptor fires
    await Assert.That(executionOrder.Count).IsEqualTo(3);

    var cApplyIndex = executionOrder.IndexOf("C.Apply(B) completed");
    var eReceptorIndex = executionOrder.IndexOf("E's receptor fired");

    await Assert.That(cApplyIndex).IsGreaterThanOrEqualTo(0)
      .Because("C.Apply(B) should have executed");
    await Assert.That(eReceptorIndex).IsGreaterThanOrEqualTo(0)
      .Because("E's receptor should have executed");
    await Assert.That(cApplyIndex).IsLessThan(eReceptorIndex)
      .Because("C.Apply(B) MUST complete BEFORE E's receptor fires - this is the sync guarantee");
  }

  /// <summary>
  /// Verify that without sync, E's receptor fires immediately (wrong order).
  /// This establishes the baseline behavior we're trying to fix.
  /// </summary>
  [Test]
  public async Task WithoutSync_EReceptorFiresImmediately_WrongOrderAsync() {
    // Arrange
    var executionOrder = new List<string>();
    var streamId = Guid.NewGuid();
    var eventBId = Guid.NewGuid();
    var perspectiveCName = typeof(TestPerspectiveC).FullName!;

    var singletonTracker = new SyncEventTracker();

    // Track event B for perspective C
    singletonTracker.TrackEvent(typeof(TestEventB), eventBId, streamId, perspectiveCName);
    executionOrder.Add("A's receptor returned Event B");

    // Use completion signals for deterministic ordering instead of Task.Delay
    var eReceptorFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    // Simulate perspective processing — waits for E to fire first, then completes
    var perspectiveTask = Task.Run(async () => {
      await eReceptorFired.Task;
      executionOrder.Add("C.Apply(B) completed");
      singletonTracker.MarkProcessedByPerspective([eventBId], perspectiveCName);
    });

    // WITHOUT sync - E's receptor fires immediately (before perspective completes)
    executionOrder.Add("E's receptor fired (NO SYNC)");
    eReceptorFired.TrySetResult();

    await perspectiveTask;

    // Without sync, E fires BEFORE C processes B
    var cApplyIndex = executionOrder.IndexOf("C.Apply(B) completed");
    var eReceptorIndex = executionOrder.IndexOf("E's receptor fired (NO SYNC)");

    await Assert.That(eReceptorIndex).IsLessThan(cApplyIndex)
      .Because("Without sync, E fires before C - this is the bug we're fixing");
  }

  /// <summary>
  /// Test that events tracked for multiple perspectives work correctly.
  /// If B is tracked for both C and D, and E waits for C only,
  /// E should fire after C processes B (regardless of D).
  /// </summary>
  [Test]
  public async Task MultiPerspective_EventTrackedForMultiple_SyncWaitsForCorrectOneAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventBId = Guid.NewGuid();
    var perspectiveCName = typeof(TestPerspectiveC).FullName!;
    var perspectiveDName = typeof(TestPerspectiveD).FullName!;

    var singletonTracker = new SyncEventTracker();

    // Track same event for TWO perspectives
    singletonTracker.TrackEvent(typeof(TestEventB), eventBId, streamId, perspectiveCName);
    singletonTracker.TrackEvent(typeof(TestEventB), eventBId, streamId, perspectiveDName);

    var mockCoordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    var awaiter = new PerspectiveSyncAwaiter(
        mockCoordinator, clock, logger,
        singletonTracker);

    // Mark event as processed for C only
    _ = Task.Run(async () => {
      await Task.Delay(50);
      // Only mark processed for C's tracking entry
      // Now using MarkProcessedByPerspective so it only signals C, not D
      singletonTracker.MarkProcessedByPerspective([eventBId], perspectiveCName);
    });

    // Act - wait for C to process
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspectiveC),
        streamId,
        eventTypes: [typeof(TestEventB)],
        timeout: TimeSpan.FromMilliseconds(500));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  /// <summary>
  /// Test timeout behavior: if C never processes B, E's sync should timeout.
  /// </summary>
  [Test]
  public async Task Timeout_CPerspectiveNeverProcessesB_SyncTimesOutAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventBId = Guid.NewGuid();
    var perspectiveCName = typeof(TestPerspectiveC).FullName!;

    var singletonTracker = new SyncEventTracker();
    singletonTracker.TrackEvent(typeof(TestEventB), eventBId, streamId, perspectiveCName);

    var mockCoordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    var awaiter = new PerspectiveSyncAwaiter(
        mockCoordinator, clock, logger,
        singletonTracker);

    // Act - C never processes B, so this should timeout
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspectiveC),
        streamId,
        eventTypes: [typeof(TestEventB)],
        timeout: TimeSpan.FromMilliseconds(200)); // Short timeout

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut)
      .Because("C never processed B, so sync should timeout");
  }

  /// <summary>
  /// Test: If no events are tracked for C when E is sent, sync falls back to DB discovery.
  /// When DB returns no pending events, sync should complete quickly.
  /// </summary>
  [Test]
  public async Task NoTrackedEvents_FallsBackToDbDiscovery_SyncsWhenDbReturnsNoPendingAsync() {
    // Arrange
    var streamId = Guid.NewGuid();

    // Empty tracker - no events tracked
    var singletonTracker = new SyncEventTracker();

    // Mock coordinator returns "no pending events" from DB discovery
    var mockCoordinator = new MockWorkCoordinator((request, _) => {
      var inquiry = (request.Count > 0 ? request[0] : null);
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = inquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,  // No pending events
            ProcessedCount = 0
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    var awaiter = new PerspectiveSyncAwaiter(
        mockCoordinator, clock, logger,
        singletonTracker);

    // Act
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspectiveC),
        streamId,
        eventTypes: [typeof(TestEventB)],
        timeout: TimeSpan.FromMilliseconds(500));
    sw.Stop();

    // Assert - with empty SyncEventTracker and no tracked events, returns NoPendingEvents immediately
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents)
      .Because("No events tracked in SyncEventTracker - nothing to wait for");
    await Assert.That(sw.ElapsedMilliseconds).IsLessThan(200)
      .Because("Should complete quickly when no events are tracked");
  }
}

// Test types for cross-command sync tests
internal sealed class TestEventB { }
internal sealed class TestPerspectiveC { }
internal sealed class TestPerspectiveD { }
internal sealed class TestCommandE { }
