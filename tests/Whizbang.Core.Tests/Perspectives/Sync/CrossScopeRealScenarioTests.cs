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

    // NO tracker - simulates Request 2 which doesn't have events in its scope
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, tracker: null);

    // Act - This is what [AwaitPerspectiveSync] does before RequestActivityStatusCommandHandler runs
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection), // Simulates ActivityFlow.Projection
      streamId,
      [typeof(FakeStartedEvent)], // Simulates ChatActivitiesContracts.StartedEvent
      timeout: TimeSpan.FromMilliseconds(200) // Short timeout for test
    );

    // Assert - Should NOT be synced because the event hasn't been processed!
    // If this fails (returns Synced), then the bug exists
    await Assert.That(result.Outcome)
      .IsEqualTo(SyncOutcome.TimedOut)
      .Because("Event is in event store but NOT processed by perspective - should timeout waiting");
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
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, tracker: null);

    // Act
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)],
      timeout: TimeSpan.FromSeconds(5)
    );

    // Assert - SHOULD be synced because event was processed
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
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
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, tracker: null);

    // Act
    var result = await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)],
      timeout: TimeSpan.FromSeconds(5)
    );

    // Assert - Should be synced (no events to wait for)
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
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

    // NO tracker and NO eventIdToAwait - simulates cross-scope scenario
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, tracker: null);

    // Act
    await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)], // Event types provided
      timeout: TimeSpan.FromSeconds(1)
    );

    // Assert - The inquiry MUST have DiscoverPendingFromOutbox = true
    await Assert.That(capturedInquiry).IsNotNull()
      .Because("A sync inquiry should be sent to the coordinator");

    await Assert.That(capturedInquiry!.DiscoverPendingFromOutbox).IsTrue()
      .Because("Cross-scope sync without explicit event IDs must discover from outbox");

    await Assert.That(capturedInquiry.EventTypeFilter).IsNotNull()
      .Because("Event type filter should be passed through");

    await Assert.That(capturedInquiry.EventTypeFilter!.Length).IsGreaterThan(0)
      .Because("Event types from attribute should be in filter");
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
    var awaiter = new PerspectiveSyncAwaiter(mockCoordinator, clock, logger, tracker: null);

    // Act - Call WaitForStreamAsync with a real Type
    await awaiter.WaitForStreamAsync(
      typeof(FakeProjection),
      streamId,
      [typeof(FakeStartedEvent)],
      timeout: TimeSpan.FromSeconds(1)
    );

    // Assert - Verify the EventTypeFilter format
    await Assert.That(capturedInquiry).IsNotNull();
    await Assert.That(capturedInquiry!.EventTypeFilter).IsNotNull();
    await Assert.That(capturedInquiry.EventTypeFilter!.Length).IsEqualTo(1);

    // THE KEY ASSERTION: Format must be "FullName, AssemblyName"
    var expectedFormat = $"{typeof(FakeStartedEvent).FullName}, {typeof(FakeStartedEvent).Assembly.GetName().Name}";
    var actualFormat = capturedInquiry.EventTypeFilter[0];

    await Assert.That(actualFormat).IsEqualTo(expectedFormat)
      .Because($"EventTypeFilter must include assembly name to match stored format. " +
               $"Expected '{expectedFormat}' but got '{actualFormat}'");

    // Also verify it contains a comma (assembly separator)
    await Assert.That(actualFormat).Contains(", ")
      .Because("The format must be 'TypeName, AssemblyName' with comma separator");
  }
}

// Fake types for testing
internal sealed class FakeProjection { }
internal sealed class FakeStartedEvent { }
