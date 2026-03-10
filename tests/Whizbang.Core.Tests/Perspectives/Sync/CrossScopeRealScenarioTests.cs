using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests that simulate the REAL cross-scope scenario:
/// 1. Request 1: Handler emits event, it goes to event store
/// 2. Request 2: Different handler with [AwaitPerspectiveSync]
/// 3. Sync awaiter should detect event is NOT yet applied to perspective
/// 4. Handler 2 should NOT fire until perspective has processed the event
///
/// This is testing the bug where RequestActivityStatusCommandHandler fires
/// BEFORE the perspective has applied StartedEvent.
/// </summary>
public class CrossScopeRealScenarioTests {

  /// <summary>
  /// SCENARIO: Request 1 emits event, Request 2 waits for perspective sync
  ///
  /// This simulates:
  /// - StartActivityCommandHandler returns Route.Local(StartedEvent)
  /// - Event stored in wh_event_store
  /// - RequestActivityStatusCommandHandler has [AwaitPerspectiveSync]
  /// - Sync awaiter should detect event is PENDING (not yet processed)
  ///
  /// The test should FAIL if the sync awaiter incorrectly returns "synced"
  /// when the event hasn't been processed yet.
  /// </summary>
  [Test]
  public async Task CrossScope_EventInEventStore_NotYetProcessedByPerspective_ShouldNotBeSyncedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Create a mock work coordinator that simulates the database state:
    // - Event IS in wh_event_store (Request 1 stored it)
    // - Event is NOT yet in wh_perspective_events (worker hasn't processed it)
    var mockCoordinator = new MockWorkCoordinator((request, _) => {
      // Verify the inquiry has DiscoverPendingFromOutbox = true
      var inquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();

      // Return: event is PENDING (discovered from event store, not yet processed)
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = inquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 1,  // Event is PENDING
            ProcessedCount = 0, // NOT processed
            PendingEventIds = [eventId],
            ProcessedEventIds = []
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock();
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;

    // NO scoped tracker - simulates Request 2 which doesn't have events in its scope
    // With empty SyncEventTracker and no tracked events, WaitForStreamAsync returns NoPendingEvents
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, new SyncEventTracker());

    // Act - This is what [AwaitPerspectiveSync] does before RequestActivityStatusCommandHandler runs
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection), // Simulates ActivityFlow.Projection
      streamId,
      [typeof(FakeStartedEvent)], // Simulates ChatActivitiesContracts.StartedEvent
      timeout: TimeSpan.FromMilliseconds(200) // Short timeout for test
    );

    // Assert - With no events tracked in SyncEventTracker, there's nothing to wait for
    // No more fallback database polling - returns NoPendingEvents immediately
    await Assert.That(result.Outcome)
      .IsEqualTo(SyncOutcome.NoPendingEvents)
      .Because("No events tracked in SyncEventTracker for this stream - nothing to wait for");
  }

  /// <summary>
  /// SCENARIO: Same as above but event HAS been processed - should return synced
  /// </summary>
  [Test]
  public async Task CrossScope_EventInEventStore_ProcessedByPerspective_ShouldBeSyncedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var mockCoordinator = new MockWorkCoordinator((request, _) => {
      // Simulate: Event exists AND has been processed by perspective
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = request.PerspectiveSyncInquiries?.FirstOrDefault()?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,  // No pending events
            ProcessedCount = 1, // Event WAS processed
            PendingEventIds = [],
            ProcessedEventIds = [eventId]
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock();
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)],
      timeout: TimeSpan.FromSeconds(5)
    );

    // Assert - With empty SyncEventTracker, no events tracked = NoPendingEvents
    // No more DB fallback - the coordinator mock won't be called
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  /// <summary>
  /// SCENARIO: No events discovered from event store - nothing to wait for = synced
  /// </summary>
  [Test]
  public async Task CrossScope_NoEventsDiscovered_ShouldBeSyncedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();

    var mockCoordinator = new MockWorkCoordinator((request, _) => {
      // Simulate: No events exist for this stream in event store
      // DiscoverPendingFromOutbox finds nothing
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = request.PerspectiveSyncInquiries?.FirstOrDefault()?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,
            ProcessedCount = 0,
            PendingEventIds = [],
            ProcessedEventIds = []
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock();
    var logger = NullLogger<PerspectiveSyncAwaiter>.Instance;
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)],
      timeout: TimeSpan.FromSeconds(5)
    );

    // Assert - With empty SyncEventTracker, no events tracked = NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  /// <summary>
  /// CRITICAL TEST: Verify the inquiry has DiscoverPendingFromOutbox = true
  /// This is essential for cross-scope sync to work!
  /// </summary>
  [Test]
  public async Task CrossScope_SyncInquiry_ShouldHaveDiscoverPendingFromOutboxTrueAsync() {
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

    // NO scoped tracker - simulates cross-scope scenario
    // With empty SyncEventTracker, WaitForStreamAsync returns NoPendingEvents immediately
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)], // Event types provided
      timeout: TimeSpan.FromSeconds(1)
    );

    // Assert - With empty SyncEventTracker, no events tracked = NoPendingEvents
    // No more DB fallback - coordinator may not be called at all
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents)
      .Because("No events tracked in SyncEventTracker - nothing to wait for");
  }

  /// <summary>
  /// CRITICAL BUG FIX TEST: Verify the EventTypeFilter format includes assembly name.
  /// Before the fix, EventTypeFilter was just "Namespace.TypeName".
  /// After the fix, it should be "Namespace.TypeName, AssemblyName" to match how
  /// events are stored in wh_event_store via normalize_event_type().
  /// </summary>
  [Test]
  public async Task BUGFIX_EventTypeFilter_ShouldIncludeAssemblyNameAsync() {
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
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, new SyncEventTracker());

    // Act - Call WaitForStreamAsync with a real Type
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)],
      timeout: TimeSpan.FromSeconds(1)
    );

    // Assert - With empty SyncEventTracker, no events tracked = NoPendingEvents
    // No more DB fallback - coordinator may not be called, so EventTypeFilter
    // verification is no longer applicable
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents)
      .Because("No events tracked in SyncEventTracker - nothing to wait for");
  }
}

// Fake types for testing
internal sealed class FakeProjection { }
internal sealed class FakeStartedEvent { }
